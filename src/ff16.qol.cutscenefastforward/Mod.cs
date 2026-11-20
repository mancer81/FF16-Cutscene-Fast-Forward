using System.Diagnostics;
using System.Globalization;
using ff16.qol.cutscenefastforward.Configuration;
using ff16.qol.cutscenefastforward.Diagnostics;
using ff16.qol.cutscenefastforward.Game;
using ff16.qol.cutscenefastforward.Input;
using ff16.qol.cutscenefastforward.UI;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace ff16.qol.cutscenefastforward;

public sealed unsafe class Mod : IMod
{
    private const int TotalSignatures = 11;
    private const double PostReleaseHintGraceSeconds = 1.25;

    [Function(CallingConventions.Microsoft)]
    private delegate byte MainFrameTickDelegate(nint framework);

    [Function(CallingConventions.Microsoft)]
    private delegate void TaskGraphExecuteFrameDelegate(nint taskGraph, FrameClock* clock);

    private IModLoader? _modLoader;
    private IReloadedHooks? _hooks;
    private Configurator? _configurator;
    private Config? _watchedConfig;
    private Config _config = new();
    private DebugLogger? _log;
    private CutsceneOverlay? _overlay;

    private readonly GameStateReader _gameState = new();
    private readonly UniversalInput _input = new();

    private IHook<MainFrameTickDelegate>? _mainFrameTickHook;
    private MainFrameTickDelegate? _mainFrameTickDelegate;
    private IHook<TaskGraphExecuteFrameDelegate>? _taskGraphExecuteHook;
    private TaskGraphExecuteFrameDelegate? _taskGraphExecuteDelegate;

    private nint _baseAddress;
    private nint _lastFrameworkPtr;
    private nint _lastTaskGraphPtr;
    private int _scansCompleted;
    private int _scansSucceeded;

    private volatile bool _suspended;
    private volatile bool _isFastForwardActive;
    private volatile bool _scaleTaskGraph;
    private ActiveSceneKind _activeFfKind;
    private double _activeAppliedSpeed = 1.0;

    private bool _isOverridingFrameClock;
    private double _savedFrameClockScale = 1.0;
    private bool _wasInputHeld;
    private bool _toggleActive;

    // Movie (.bk2) pacing state
    private long _ffMovieLastTickTimestamp;
    private double _ffMoviePendingMs;
    private uint _ffMovieStartFrame;
    private uint _ffMovieWindowStartFrame;
    private long _ffMovieTotalAdvancedMs;
    private int _ffMovieBackpressureFrames;

    // Overlay auto-hide & state tracking
    private bool _wasSceneEligible;
    private ActiveSceneKind _lastEligibleKind;
    private long _sceneBecameEligibleTimestamp;
    private long _lastFfEndedTimestamp;
    private long _lastOverlayDetailTimestamp;
    private bool _lastOverlayFfState;
    private bool _lastOverlayVisible;
    private ActiveSceneKind _lastOverlayKind;

    // Diagnostics & telemetry state
    private bool _loggedFirstFrame;
    private bool _loggedFirstTaskGraph;
    private PointerDiagnostics _lastPtrDiag;
    private bool _hasLoggedScene;
    private SceneSnapshot _lastLoggedScene;
    private bool _wasAnySceneActive;

    private long _ffStartTimestamp;
    private long _ffLastHeartbeatTimestamp;
    private float _ffStartTimelineTime;
    private float _ffWindowStartTimelineTime;
    private int _ffTotalFrames;
    private int _ffWindowFrames;
    private long _ffWindowClampedTicksSum;
    private long _ffWindowRawTicksSum;
    private int _taskGraphScaledCalls;
    private int _hookExceptionCount;

    public Action Disposing { get; } = () => { };

    public void Start(IModLoaderV1 loader)
    {
        Initialize(loader, null);
    }

    public void StartEx(IModLoaderV1 loader, IModConfigV1 modConfig)
    {
        Initialize(loader, modConfig);
    }

    private void Initialize(IModLoaderV1 loaderV1, IModConfigV1? modConfig)
    {
        _modLoader = (IModLoader)loaderV1;
        var consoleLogger = (ILogger)_modLoader.GetLogger();

        using var process = Process.GetCurrentProcess();
        ProcessModule? mainModule = process.MainModule;
        _baseAddress = mainModule?.BaseAddress ?? 0;

        string modId = modConfig?.ModId ?? "ff16.qol.cutscenefastforward";
        string configDir = _modLoader.GetModConfigDirectory(modId);
        string modDir = _modLoader.GetDirectoryForModId(modId);

        _configurator = new Configurator(configDir);
        _configurator.SetModDirectory(modDir);
        _watchedConfig = _configurator.GetConfiguration<Config>(0);
        _config = _watchedConfig;
        _watchedConfig.ConfigurationUpdated += OnConfigurationUpdated;

        _log = new DebugLogger(consoleLogger, configDir, _config.DebugLogging, _config.DebugLogToFile);
        _overlay = new CutsceneOverlay(_log);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _log?.Flush();

        LogStartupBanner(process, mainModule, modConfig, configDir);

        if (!_modLoader.GetController<IReloadedHooks>().TryGetTarget(out _hooks) || _hooks == null)
        {
            _log.Error("Failed to acquire IReloadedHooks. Ensure 'reloaded.sharedlib.hooks' is installed and enabled.");
            return;
        }

        if (!_modLoader.GetController<IStartupScanner>().TryGetTarget(out var scanner) || scanner == null)
        {
            _log.Error("Failed to acquire IStartupScanner. Ensure 'Reloaded.Memory.SigScan.ReloadedII' is installed and enabled.");
            return;
        }

        RegisterSignatureScans(scanner);
    }

    private void LogStartupBanner(Process process, ProcessModule? mainModule, IModConfigV1? modConfig, string configDir)
    {
        string version = modConfig?.ModVersion ?? typeof(Mod).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        string exePath = mainModule?.FileName ?? "<unknown>";
        string fileVer = mainModule?.FileVersionInfo?.FileVersion ?? "<unknown>";
        long exeSize = mainModule?.ModuleMemorySize ?? 0;

        _log!.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Cutscene Fast-Forward v{0} loaded (Speed={1:0.##}x, Movie={2:0.##}x, Mode={3})",
            version, _config.GetClampedSpeed(), _config.GetClampedMovieSpeed(), _config.ActivationMode));

        _log.Debug($"Process: {process.ProcessName} (PID={process.Id}), Base=0x{_baseAddress:X}, ModuleSize=0x{exeSize:X}, FileVer={fileVer}");
        _log.Debug($"ExePath: {exePath}");
        _log.Debug($"ConfigDir: {configDir}");
        if (_config.DebugLogging && _config.DebugLogToFile)
            _log.Debug($"DebugLogFile: {_log.LogFilePath}");
        _log.Debug(FormatConfigSummary(_config));

        if (_config.DebugLogging && _modLoader != null)
        {
            try
            {
                var activeMods = _modLoader.GetActiveMods();
                string modList = string.Join(", ", activeMods.Select(m => $"{m.Generic.ModId}({m.Generic.ModVersion})"));
                _log.Debug($"Active Reloaded-II mods ({activeMods.Length}): {modList}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not enumerate active mods: {ex.Message}");
            }
        }
    }

    private static string FormatConfigSummary(Config cfg) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Config: Enabled={0}, Speed={1:0.##}x, MovieSpeed={2:0.##}x, Mode={3}, StdSkip={4}, ExtraKey={5}, ExtraPad={6}, " +
            "Overlay={7}({8}, scale={9:0.##}, op={10:0.##}, autoHide={11:0.#}s), Scope=[Cutscenes={12}, Movies={13}, SideScenes={14}, SimpleEvents={15}], " +
            "Debug={16} (LogFile={17}, InputTrace={18})",
            cfg.Enabled, cfg.GetClampedSpeed(), cfg.GetClampedMovieSpeed(), cfg.ActivationMode,
            cfg.UseStandardSkipButton, cfg.ExtraKeyboardKey, cfg.ExtraGamepadButton,
            cfg.ShowOverlayIndicator, cfg.OverlayCorner, cfg.GetClampedOverlayScale(), cfg.GetClampedOpacity(), cfg.GetClampedAutoHideSeconds(),
            cfg.ApplyToInEngineCutscenes, cfg.ApplyToMovies, cfg.ApplyToSideScenesAndFlyovers, cfg.ApplyToSimpleEvents,
            cfg.DebugLogging, cfg.DebugLogToFile, cfg.DebugTraceSceneInputs);

    private void OnConfigurationUpdated(IUpdatableConfigurable updated)
    {
        _config = (Config)updated;
        _log?.UpdateSettings(_config.DebugLogging, _config.DebugLogToFile);
        _log?.Info($"Configuration reloaded -> Speed={_config.GetClampedSpeed():0.##}x, Movie={_config.GetClampedMovieSpeed():0.##}x, Mode={_config.ActivationMode}");
        _log?.Debug(FormatConfigSummary(_config));
    }

    private void RecordScanComplete(bool ok)
    {
        if (ok)
            Interlocked.Increment(ref _scansSucceeded);

        if (Interlocked.Increment(ref _scansCompleted) == TotalSignatures)
        {
            int succeeded = Volatile.Read(ref _scansSucceeded);
            if (succeeded == TotalSignatures)
                _log?.Info($"Ready ({succeeded}/{TotalSignatures} hooks & signatures verified).");
            else
                _log?.Warn($"Initialized with partial signatures ({succeeded}/{TotalSignatures} verified). Check warnings above.");
        }
    }

    private void RegisterSignatureScans(IStartupScanner scanner)
    {
        scanner.AddMainModuleScan(Signatures.MainFrameTick, result =>
        {
            if (!result.Found)
            {
                _log!.Error("Signature NOT FOUND: MainFrameTick (mod cannot run on this game version)");
                RecordScanComplete(false);
                return;
            }

            nint addr = _baseAddress + result.Offset;
            _mainFrameTickDelegate = MainFrameTickImpl;
            _mainFrameTickHook = _hooks!.CreateHook(_mainFrameTickDelegate, addr).Activate();
            _log!.Debug($"[Sig 1/11 OK] MainFrameTick hooked @ +0x{result.Offset:X} (0x{addr:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.TaskGraphExecuteFrame, result =>
        {
            if (!result.Found)
            {
                _log!.Error("Signature NOT FOUND: TaskGraphExecuteFrame (cutscene TaskGraph scaling unavailable)");
                RecordScanComplete(false);
                return;
            }

            nint addr = _baseAddress + result.Offset;
            _taskGraphExecuteDelegate = TaskGraphExecuteFrameImpl;
            _taskGraphExecuteHook = _hooks!.CreateHook(_taskGraphExecuteDelegate, addr).Activate();
            _log!.Debug($"[Sig 2/11 OK] TaskGraph::ExecuteFrame hooked @ +0x{result.Offset:X} (0x{addr:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.EventMgrGlobalRef, result =>
        {
            if (!result.Found)
            {
                _log!.Error("Signature NOT FOUND: EventMgrGlobalRef");
                RecordScanComplete(false);
                return;
            }

            nint instrAddr = _baseAddress + result.Offset;
            _gameState.EventMgrGlobalAddr = Signatures.ResolveRipRelative(instrAddr);
            _log!.Debug($"[Sig 3/11 OK] g_EventManager @ +0x{_gameState.EventMgrGlobalAddr - _baseAddress:X} (ref +0x{result.Offset:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.EventMgrIsActive, result =>
        {
            if (!result.Found)
            {
                _log!.Error("Signature NOT FOUND: EventMgrIsActive");
                RecordScanComplete(false);
                return;
            }

            nint addr = _baseAddress + result.Offset;
            _gameState.EventMgrIsActiveFn = (delegate* unmanaged[Stdcall]<nint, byte>)addr;
            _log!.Debug($"[Sig 4/11 OK] EventManager::IsActive @ +0x{result.Offset:X}");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.EventIsBlockingCutscene, result =>
        {
            if (!result.Found)
            {
                _log!.Error("Signature NOT FOUND: EventIsBlockingCutscene");
                RecordScanComplete(false);
                return;
            }

            nint addr = _baseAddress + result.Offset;
            _gameState.EventIsBlockingCutsceneFn = (delegate* unmanaged[Stdcall]<nint, byte>)addr;
            _log!.Debug($"[Sig 5/11 OK] Event::IsBlockingCutscene @ +0x{result.Offset:X}");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.EventCanPause, result =>
        {
            if (!result.Found)
            {
                _log!.Warn("[Sig 6/11 MISS] EventCanPause not found (diagnostic only)");
                RecordScanComplete(false);
                return;
            }

            nint addr = _baseAddress + result.Offset;
            _gameState.EventCanPauseFn = (delegate* unmanaged[Stdcall]<nint, byte>)addr;
            _log!.Debug($"[Sig 6/11 OK] Event::CanPause @ +0x{result.Offset:X}");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.SimpleEventMgrRef, result =>
        {
            if (!result.Found)
            {
                _log!.Warn("[Sig 7/11 MISS] SimpleEventMgrRef not found (SimpleEvent detection unavailable)");
                RecordScanComplete(false);
                return;
            }

            nint instrAddr = _baseAddress + result.Offset;
            _gameState.SimpleEventMgrGlobalAddr = Signatures.ResolveRipRelative(instrAddr);
            _log!.Debug($"[Sig 7/11 OK] g_SimpleEventManager @ +0x{_gameState.SimpleEventMgrGlobalAddr - _baseAddress:X} (ref +0x{result.Offset:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.InputStateGlobalRef, result =>
        {
            if (!result.Found)
            {
                _log!.Warn("[Sig 8/11 MISS] InputStateGlobalRef not found (falling back to ExtraKeyboardKey / ExtraGamepadButton)");
                RecordScanComplete(false);
                return;
            }

            nint instrAddr = _baseAddress + result.Offset;
            _gameState.InputStateGlobalAddr = Signatures.ResolveRipRelative(instrAddr);
            _log!.Debug($"[Sig 8/11 OK] g_InputState @ +0x{_gameState.InputStateGlobalAddr - _baseAddress:X} (ref +0x{result.Offset:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.CutsceneModeUpdater, result =>
        {
            if (!result.Found)
            {
                _log!.Warn("[Sig 9/11 MISS] CutsceneModeUpdater not found (diagnostic only)");
                RecordScanComplete(false);
                return;
            }

            nint instrAddr = _baseAddress + result.Offset + 0x12;
            _gameState.CutsceneModeGlobalAddr = Signatures.ResolveRipRelative(instrAddr);
            _log!.Debug($"[Sig 9/11 OK] g_CutsceneModeState @ +0x{_gameState.CutsceneModeGlobalAddr - _baseAddress:X} (ref +0x{result.Offset:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.MovieMgrGlobalRef, result =>
        {
            if (!result.Found)
            {
                _log!.Warn("[Sig 10/11 MISS] MovieMgrGlobalRef not found (.bk2 movie fast-forward unavailable)");
                RecordScanComplete(false);
                return;
            }

            nint instrAddr = _baseAddress + result.Offset + 0x04;
            _gameState.MovieMgrGlobalAddr = Signatures.ResolveRipRelative(instrAddr);
            _log!.Debug($"[Sig 10/11 OK] g_MovieManager @ +0x{_gameState.MovieMgrGlobalAddr - _baseAddress:X} (ref +0x{result.Offset:X})");
            RecordScanComplete(true);
        });

        scanner.AddMainModuleScan(Signatures.FieldAndControlLockRef, result =>
        {
            if (!result.Found)
            {
                _log!.Warn("[Sig 11/11 MISS] FieldAndControlLockRef not found (optional side-scene detection reduced)");
                RecordScanComplete(false);
                return;
            }

            nint baseInstr = _baseAddress + result.Offset;
            _gameState.FieldStateGlobalAddr = Signatures.ResolveRipRelative(baseInstr);
            _gameState.ControlLockGlobalAddr = Signatures.ResolveRipRelative(baseInstr + 0x1E);
            _log!.Debug(
                $"[Sig 11/11 OK] g_FieldState @ +0x{_gameState.FieldStateGlobalAddr - _baseAddress:X}, " +
                $"g_ControlLock @ +0x{_gameState.ControlLockGlobalAddr - _baseAddress:X} (ref +0x{result.Offset:X})");
            RecordScanComplete(true);
        });
    }

    private void TaskGraphExecuteFrameImpl(nint taskGraph, FrameClock* clock)
    {
        _lastTaskGraphPtr = taskGraph;

        if (!_loggedFirstTaskGraph && _config.DebugLogging && taskGraph != 0)
        {
            _loggedFirstTaskGraph = true;
            float pCh0 = *(float*)(taskGraph + GameStateReader.TaskGraphPendingChannel0Offset);
            float pCh1 = *(float*)(taskGraph + GameStateReader.TaskGraphPendingChannel1Offset);
            float ch0 = *(float*)(taskGraph + GameStateReader.TaskGraphChannel0Offset);
            float ch1 = *(float*)(taskGraph + GameStateReader.TaskGraphChannel1Offset);
            uint pauseMask = *(uint*)(taskGraph + GameStateReader.TaskGraphPauseMaskOffset);
            int pauseOvr = *(int*)(taskGraph + GameStateReader.TaskGraphPauseOverrideOffset);
            byte initFixedFlag = *(byte*)(taskGraph + GameStateReader.TaskGraphFixedDtFlagOffset);
            float initFixedDt = *(float*)(taskGraph + GameStateReader.TaskGraphFixedDtValOffset);

            _log?.Debug(string.Format(
                CultureInfo.InvariantCulture,
                "First TaskGraph::ExecuteFrame: TG=0x{0:X}, T{1:D2}, PendingCh0={2:0.###}, PendingCh1={3:0.###}, ActiveCh0={4:0.###}, ActiveCh1={5:0.###}, PauseMask=0x{6:X}, PauseOvr={7}, FixedDtFlag={8}, FixedDt={9:0.####}",
                taskGraph, Environment.CurrentManagedThreadId, pCh0, pCh1, ch0, ch1, pauseMask, pauseOvr, initFixedFlag, initFixedDt));
        }

        if (!_isFastForwardActive || !_scaleTaskGraph || _suspended || taskGraph == 0)
        {
            _taskGraphExecuteHook!.OriginalFunction(taskGraph, clock);
            return;
        }

        if (GameStateReader.IsTaskGraphEventPaused(taskGraph, out _))
        {
            _taskGraphExecuteHook!.OriginalFunction(taskGraph, clock);
            return;
        }

        double speed = _activeAppliedSpeed;
        if (!(speed > 1.001) || !double.IsFinite(speed))
        {
            _taskGraphExecuteHook!.OriginalFunction(taskGraph, clock);
            return;
        }

        float fSpeed = (float)speed;
        float* pendingCh1Ptr = (float*)(taskGraph + GameStateReader.TaskGraphPendingChannel1Offset);
        float* activeCh1Ptr = (float*)(taskGraph + GameStateReader.TaskGraphChannel1Offset);
        float savedPendingCh1 = *pendingCh1Ptr;
        float savedActiveCh1 = *activeCh1Ptr;
        float scaledPendingCh1 = savedPendingCh1 * fSpeed;
        float scaledActiveCh1 = savedActiveCh1 * fSpeed;

        byte fixedFlag = *(byte*)(taskGraph + GameStateReader.TaskGraphFixedDtFlagOffset);
        float* pendingFixedDtPtr = (float*)(taskGraph + GameStateReader.TaskGraphPendingFixedDtValOffset);
        float* activeFixedDtPtr = (float*)(taskGraph + GameStateReader.TaskGraphFixedDtValOffset);
        float savedPendingFixedDt = *pendingFixedDtPtr;
        float savedActiveFixedDt = *activeFixedDtPtr;

        try
        {
            // Scale BOTH Active Channel 1 (tasks 0..0x8C) AND Pending Channel 1
            // (copied to Active by Task 0x8D TimeController::Update right before EventManager::Update at Task 0xB2).
            *pendingCh1Ptr = scaledPendingCh1;
            *activeCh1Ptr = scaledActiveCh1;

            if (fixedFlag != 0)
            {
                if (savedPendingFixedDt > 0f) *pendingFixedDtPtr = savedPendingFixedDt * fSpeed;
                if (savedActiveFixedDt > 0f) *activeFixedDtPtr = savedActiveFixedDt * fSpeed;
            }

            _taskGraphScaledCalls++;
            _taskGraphExecuteHook!.OriginalFunction(taskGraph, clock);

            if (_taskGraphScaledCalls == 1 && _config.DebugLogging)
            {
                _log?.Debug(string.Format(
                    CultureInfo.InvariantCulture,
                    "TaskGraph first scaled tick result: PendingCh1 {0:0.###}->{1:0.###} (post={2:0.###}), ActiveCh1 {3:0.###}->{4:0.###} (post={5:0.###})",
                    savedPendingCh1, scaledPendingCh1, *pendingCh1Ptr,
                    savedActiveCh1, scaledActiveCh1, *activeCh1Ptr));
            }
        }
        finally
        {
            if (MathF.Abs(*pendingCh1Ptr - scaledPendingCh1) < 0.001f)
                *pendingCh1Ptr = savedPendingCh1;

            *activeCh1Ptr = savedActiveCh1;

            if (fixedFlag != 0)
            {
                *pendingFixedDtPtr = savedPendingFixedDt;
                *activeFixedDtPtr = savedActiveFixedDt;
            }
        }
    }

    private byte MainFrameTickImpl(nint framework)
    {
        FrameClock* clock = null;
        Config cfg = _config;

        try
        {
            _lastFrameworkPtr = framework;
            clock = _gameState.GetFrameClock(framework);

            if (cfg.DebugLogging)
                LogPointerChangesIfNeeded(framework, clock);

            if (clock == null || !_gameState.IsReady || _suspended || !cfg.Enabled)
            {
                StopFastForwardIfNeeded(clock, default, "disabled/suspended/not-ready");
                _toggleActive = false;
                _wasInputHeld = false;
                _wasSceneEligible = false;
                _lastEligibleKind = ActiveSceneKind.None;
                HideOverlay();
                return _mainFrameTickHook!.OriginalFunction(framework);
            }

            SceneSnapshot scene = _gameState.ReadSnapshot(_lastTaskGraphPtr);
            ActiveSceneKind activeKind = scene.ResolveActiveKind(
                cfg.ApplyToInEngineCutscenes,
                cfg.ApplyToMovies,
                cfg.ApplyToSideScenesAndFlyovers,
                cfg.ApplyToSimpleEvents);

            if (cfg.DebugLogging)
            {
                LogSceneTransitionIfNeeded(scene, activeKind);
                TraceSceneInputsIfNeeded(scene, activeKind, cfg);
            }

            bool isEligible = activeKind != ActiveSceneKind.None;
            bool shouldFastForward = false;
            InputTriggerSource triggers = InputTriggerSource.None;

            if (!isEligible)
            {
                _toggleActive = false;
                _wasInputHeld = false;
                _wasSceneEligible = false;
                _lastEligibleKind = ActiveSceneKind.None;
            }
            else
            {
                if (!_wasSceneEligible || _lastEligibleKind != activeKind)
                {
                    _wasSceneEligible = true;
                    _lastEligibleKind = activeKind;
                    _sceneBecameEligibleTimestamp = Stopwatch.GetTimestamp();
                    _lastFfEndedTimestamp = 0;
                }

                byte* inputSnapshot = _gameState.GetUnifiedInputSnapshot();
                triggers = _input.PollFastForwardTriggers(
                    inputSnapshot,
                    cfg.UseStandardSkipButton,
                    cfg.ExtraKeyboardKey,
                    cfg.ExtraGamepadButton);

                bool inputHeld = triggers != InputTriggerSource.None;
                if (cfg.ActivationMode == ActivationMode.Hold)
                {
                    shouldFastForward = inputHeld;
                }
                else
                {
                    if (inputHeld && !_wasInputHeld)
                        _toggleActive = !_toggleActive;
                    shouldFastForward = _toggleActive;
                }

                _wasInputHeld = inputHeld;
            }

            if (shouldFastForward)
            {
                ApplyFastForward(clock, cfg, scene, activeKind, triggers);
            }
            else
            {
                StopFastForwardIfNeeded(clock, scene, isEligible ? "button released" : (scene.IsAnyPauseActive ? "paused" : "scene ended"));
            }

            UpdateOverlay(cfg, scene, activeKind, shouldFastForward);
        }
        catch (Exception ex)
        {
            HandleHookException("pre-tick", ex, clock);
        }

        byte result = _mainFrameTickHook!.OriginalFunction(framework);

        try
        {
            if (_isFastForwardActive && clock != null)
            {
                SceneSnapshot postScene = _gameState.ReadSnapshot(_lastTaskGraphPtr);
                ActiveSceneKind postKind = postScene.ResolveActiveKind(
                    cfg.ApplyToInEngineCutscenes,
                    cfg.ApplyToMovies,
                    cfg.ApplyToSideScenesAndFlyovers,
                    cfg.ApplyToSimpleEvents);

                if (postKind == ActiveSceneKind.None)
                {
                    _toggleActive = false;
                    _wasInputHeld = false;
                    _wasSceneEligible = false;
                    _lastEligibleKind = ActiveSceneKind.None;
                    StopFastForwardIfNeeded(clock, postScene, postScene.IsAnyPauseActive ? "post-tick paused" : "post-tick scene ended");
                    UpdateOverlay(cfg, postScene, ActiveSceneKind.None, isFastForwarding: false);
                    if (cfg.DebugLogging)
                        LogSceneTransitionIfNeeded(postScene, postKind);
                }
            }
        }
        catch (Exception ex)
        {
            HandleHookException("post-tick", ex, clock);
        }

        return result;
    }

    private void ApplyFastForward(
        FrameClock* clock,
        Config cfg,
        in SceneSnapshot scene,
        ActiveSceneKind kind,
        InputTriggerSource triggers)
    {
        if (_isFastForwardActive && _activeFfKind != kind)
            StopFastForwardIfNeeded(clock, scene, $"mode switch {_activeFfKind}->{kind}");

        if (kind == ActiveSceneKind.Movie)
        {
            ApplyMovieFastForward(cfg, scene, triggers);
            return;
        }

        double targetSpeed = cfg.GetClampedSpeed();
        long now = Stopwatch.GetTimestamp();

        if (!_isFastForwardActive)
        {
            double current = clock->TimeScale;
            _savedFrameClockScale = double.IsFinite(current) && current > 0.0 && current <= 100.0 ? current : 1.0;
            _activeAppliedSpeed = targetSpeed;
            _activeFfKind = kind;
            _scaleTaskGraph = true;
            _isFastForwardActive = true;
            _isOverridingFrameClock = true;
            _ffStartTimestamp = now;
            _ffLastHeartbeatTimestamp = now;
            _ffStartTimelineTime = scene.TimelineCurrentTime;
            _ffWindowStartTimelineTime = scene.TimelineCurrentTime;
            _ffTotalFrames = 0;
            _ffWindowFrames = 0;
            _ffWindowClampedTicksSum = 0;
            _ffWindowRawTicksSum = 0;
            _taskGraphScaledCalls = 0;

            _log?.Debug(string.Format(
                CultureInfo.InvariantCulture,
                "Fast-Forward ON [{0}] -> {1:0.##}x (saved={2:0.###}x, trigger={3}, CutsceneId=0x{4:X}, t={5:0.00}s/{6:0.0}s, SyncBlocker=0x{7:X}, EvSpeed={8:0.##}, TG=0x{9:X})",
                kind, targetSpeed, _savedFrameClockScale, triggers, scene.CutsceneId,
                scene.TimelineCurrentTime, scene.TimelineTotalSeconds, scene.TimelineSyncBlocker,
                scene.EventPlaybackSpeed, _lastTaskGraphPtr));
        }
        else if (Math.Abs(_activeAppliedSpeed - targetSpeed) > 0.001)
        {
            _activeAppliedSpeed = targetSpeed;
            _log?.Debug(string.Format(CultureInfo.InvariantCulture, "Fast-Forward speed updated -> {0:0.##}x", targetSpeed));
        }

        clock->TimeScale = targetSpeed;

        _ffTotalFrames++;
        _ffWindowFrames++;
        _ffWindowClampedTicksSum += clock->ClampedDeltaTicks;
        _ffWindowRawTicksSum += clock->RawDeltaTicks;

        if (cfg.DebugLogging)
        {
            double windowSec = (now - _ffLastHeartbeatTimestamp) / (double)Stopwatch.Frequency;
            if (windowSec >= 1.0 && _ffWindowFrames > 0)
            {
                double fps = _ffWindowFrames / windowSec;
                double avgClampedMs = (_ffWindowClampedTicksSum / (double)_ffWindowFrames) / 300.0;
                double totalWallSec = (now - _ffStartTimestamp) / (double)Stopwatch.Frequency;
                float tlDelta = scene.TimelineCurrentTime - _ffWindowStartTimelineTime;
                double effectiveTlRatio = windowSec > 0.01 ? tlDelta / windowSec : 0.0;

                _log?.DebugFileOnly(string.Format(
                    CultureInfo.InvariantCulture,
                    "FF Heartbeat [{0}]: wall={1:0.0}s, fps={2:0.0}, target={3:0.##}x, effectiveTimeline={4:0.00}x (t={5:0.00}s/{6:0.0}s), avgDt={7:0.00}ms, tgScaled={8}, SyncBlocker=0x{9:X}, Flags=0x{10:X}",
                    kind, totalWallSec, fps, targetSpeed, effectiveTlRatio,
                    scene.TimelineCurrentTime, scene.TimelineTotalSeconds,
                    avgClampedMs, _taskGraphScaledCalls, scene.TimelineSyncBlocker, scene.RawEventFlags));

                _ffLastHeartbeatTimestamp = now;
                _ffWindowStartTimelineTime = scene.TimelineCurrentTime;
                _ffWindowFrames = 0;
                _ffWindowClampedTicksSum = 0;
                _ffWindowRawTicksSum = 0;
            }
        }
    }

    private void ApplyMovieFastForward(Config cfg, in SceneSnapshot scene, InputTriggerSource triggers)
    {
        double targetSpeed = cfg.GetClampedMovieSpeed();
        long now = Stopwatch.GetTimestamp();

        if (!_isFastForwardActive)
        {
            _activeAppliedSpeed = targetSpeed;
            _activeFfKind = ActiveSceneKind.Movie;
            _scaleTaskGraph = false;
            _isFastForwardActive = true;
            _ffStartTimestamp = now;
            _ffLastHeartbeatTimestamp = now;
            _ffMovieLastTickTimestamp = now;
            _ffMoviePendingMs = 0.0;
            _ffMovieStartFrame = scene.Movie.CurrentFrame;
            _ffMovieWindowStartFrame = scene.Movie.CurrentFrame;
            _ffMovieTotalAdvancedMs = 0;
            _ffMovieBackpressureFrames = 0;
            _ffTotalFrames = 0;
            _ffWindowFrames = 0;

            _log?.Debug(string.Format(
                CultureInfo.InvariantCulture,
                "Fast-Forward ON [Movie] -> {0:0.##}x (trigger={1}, BinkPlayer=0x{2:X}, f={3}/{4} @ {5:0.##}fps, ms={6}, CutsceneId=0x{7:X})",
                targetSpeed, triggers, scene.Movie.BinkPlayerPtr,
                scene.Movie.CurrentFrame, scene.Movie.TotalFrames, scene.Movie.Fps,
                scene.Movie.CurrentMs, scene.CutsceneId));
        }
        else if (Math.Abs(_activeAppliedSpeed - targetSpeed) > 0.001)
        {
            _activeAppliedSpeed = targetSpeed;
            _log?.Debug(string.Format(CultureInfo.InvariantCulture, "Fast-Forward [Movie] speed updated -> {0:0.##}x", targetSpeed));
        }

        double wallDtMs = Math.Clamp(
            (now - _ffMovieLastTickTimestamp) * 1000.0 / Stopwatch.Frequency,
            0.0,
            100.0);
        _ffMovieLastTickTimestamp = now;

        if (targetSpeed > 1.001)
        {
            _ffMoviePendingMs += wallDtMs * (targetSpeed - 1.0);
            int wholeMs = (int)Math.Floor(_ffMoviePendingMs);
            if (wholeMs >= 1)
            {
                int applied = GameStateReader.AdvanceBinkMovieStartTime(scene.Movie, wholeMs, out bool backpressured);
                if (applied > 0)
                {
                    _ffMoviePendingMs -= applied;
                    _ffMovieTotalAdvancedMs += applied;
                }
                else if (backpressured)
                {
                    _ffMoviePendingMs = 0.0;
                    _ffMovieBackpressureFrames++;
                }
            }
        }

        _ffTotalFrames++;
        _ffWindowFrames++;

        if (cfg.DebugLogging)
        {
            double windowSec = (now - _ffLastHeartbeatTimestamp) / (double)Stopwatch.Frequency;
            if (windowSec >= 1.0 && _ffWindowFrames > 0)
            {
                double fps = _ffWindowFrames / windowSec;
                double totalWallSec = (now - _ffStartTimestamp) / (double)Stopwatch.Frequency;
                long frameDelta = (long)scene.Movie.CurrentFrame - _ffMovieWindowStartFrame;
                double movieSecDelta = scene.Movie.Fps > 0.01 ? frameDelta / scene.Movie.Fps : 0.0;
                double effectiveRatio = windowSec > 0.01 ? movieSecDelta / windowSec : 0.0;

                _log?.DebugFileOnly(string.Format(
                    CultureInfo.InvariantCulture,
                    "FF Heartbeat [Movie]: wall={0:0.0}s, gameFps={1:0.0}, target={2:0.##}x, effectiveMovie={3:0.00}x (f={4}/{5}, {6:0.0}s/{7:0.0}s), advMs={8}, bpFrames={9}",
                    totalWallSec, fps, targetSpeed, effectiveRatio,
                    scene.Movie.CurrentFrame, scene.Movie.TotalFrames,
                    scene.Movie.CurrentSeconds, scene.Movie.TotalSeconds,
                    _ffMovieTotalAdvancedMs, _ffMovieBackpressureFrames));

                _ffLastHeartbeatTimestamp = now;
                _ffMovieWindowStartFrame = scene.Movie.CurrentFrame;
                _ffWindowFrames = 0;
            }
        }
    }

    private void StopFastForwardIfNeeded(FrameClock* clock, in SceneSnapshot scene, string reason)
    {
        if (!_isFastForwardActive && !_isOverridingFrameClock)
            return;

        ActiveSceneKind endedKind = _activeFfKind;
        _isFastForwardActive = false;
        _scaleTaskGraph = false;
        _activeFfKind = ActiveSceneKind.None;
        _ffMoviePendingMs = 0.0;

        long now = Stopwatch.GetTimestamp();
        if (reason == "button released")
            _lastFfEndedTimestamp = now;

        if (_isOverridingFrameClock)
        {
            if (clock != null)
                clock->TimeScale = _savedFrameClockScale;
            _isOverridingFrameClock = false;
        }

        double durationSec = (now - _ffStartTimestamp) / (double)Stopwatch.Frequency;

        if (endedKind == ActiveSceneKind.Movie)
        {
            long frameDelta = scene.Movie.CurrentFrame >= _ffMovieStartFrame
                ? (long)scene.Movie.CurrentFrame - _ffMovieStartFrame
                : 0;
            double movieSecDelta = scene.Movie.Fps > 0.01 ? frameDelta / scene.Movie.Fps : 0.0;
            double effectiveRatio = durationSec > 0.05 && movieSecDelta >= 0 ? movieSecDelta / durationSec : 0.0;

            _log?.Debug(string.Format(
                CultureInfo.InvariantCulture,
                "Fast-Forward OFF [Movie] (reason={0}, wall={1:0.00}s, movieDelta={2:+0.00;-0.00;0.00}s [{3:0.00}x], fDelta={4}, advMs={5}, bpFrames={6}, frames={7})",
                reason, durationSec, movieSecDelta, effectiveRatio, frameDelta,
                _ffMovieTotalAdvancedMs, _ffMovieBackpressureFrames, _ffTotalFrames));
            return;
        }

        float tlDelta = scene.TimelineCurrentTime - _ffStartTimelineTime;
        double effectiveRatioTl = durationSec > 0.05 && tlDelta >= 0 ? tlDelta / durationSec : 0.0;

        _log?.Debug(string.Format(
            CultureInfo.InvariantCulture,
            "Fast-Forward OFF [{0}] -> {1:0.###}x (reason={2}, wall={3:0.00}s, tlDelta={4:+0.00;-0.00;0.00}s [{5:0.00}x], frames={6}, tgScaled={7})",
            endedKind, _savedFrameClockScale, reason, durationSec, tlDelta, effectiveRatioTl, _ffTotalFrames, _taskGraphScaledCalls));
    }

    private void UpdateOverlay(Config cfg, in SceneSnapshot scene, ActiveSceneKind activeKind, bool isFastForwarding)
    {
        if (_overlay == null)
            return;

        if (!cfg.ShowOverlayIndicator || activeKind == ActiveSceneKind.None)
        {
            HideOverlay();
            return;
        }

        // Auto-hide the idle hint after OverlayAutoHideSeconds (default 4s) so dramatic cutscenes stay clean,
        // while always showing immediately while fast-forwarding or when Debug Mode is enabled.
        if (!isFastForwarding && !cfg.DebugLogging)
        {
            double autoHideSec = cfg.GetClampedAutoHideSeconds();
            if (autoHideSec > 0.01)
            {
                long nowTs = Stopwatch.GetTimestamp();
                double sinceSceneStart = (nowTs - _sceneBecameEligibleTimestamp) / (double)Stopwatch.Frequency;
                bool inPostReleaseGrace = _lastFfEndedTimestamp != 0 &&
                    ((nowTs - _lastFfEndedTimestamp) / (double)Stopwatch.Frequency) < PostReleaseHintGraceSeconds;

                if (sinceSceneStart >= autoHideSec && !inPostReleaseGrace)
                {
                    HideOverlay();
                    return;
                }
            }
        }

        bool stateTransition = !_lastOverlayVisible || _lastOverlayFfState != isFastForwarding || _lastOverlayKind != activeKind;
        string hint = BuildHintLabel(cfg);
        string modeTag = activeKind switch
        {
            ActiveSceneKind.Movie => "Movie",
            ActiveSceneKind.SideScene => "Scene",
            _ => string.Empty
        };
        double speed = activeKind == ActiveSceneKind.Movie ? cfg.GetClampedMovieSpeed() : cfg.GetClampedSpeed();
        string debugDetail = string.Empty;

        if (cfg.DebugLogging)
        {
            long now = Stopwatch.GetTimestamp();
            double sinceLastMs = (now - _lastOverlayDetailTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (!stateTransition && sinceLastMs < 100.0)
                return;

            _lastOverlayDetailTimestamp = now;
            if (activeKind == ActiveSceneKind.Movie)
            {
                debugDetail = string.Format(
                    CultureInfo.InvariantCulture,
                    "f {0}/{1} \u00B7 {2:0.0}s/{3:0.0}s",
                    scene.Movie.CurrentFrame, scene.Movie.TotalFrames,
                    scene.Movie.CurrentSeconds, scene.Movie.TotalSeconds);
            }
            else if (scene.TimelineTotalSeconds > 0.1f)
            {
                debugDetail = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.0}s/{1:0.0}s \u00B7 0x{2:X}",
                    scene.TimelineCurrentTime, scene.TimelineTotalSeconds, scene.CutsceneId);
            }
            else if (scene.EventActive || scene.SecondaryEventCount > 0)
            {
                uint id = scene.EventActive ? scene.CutsceneId : scene.SecondaryEventId;
                debugDetail = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.0}s \u00B7 0x{1:X}",
                    scene.TimelineCurrentTime, id);
            }
            else
            {
                debugDetail = string.Format(
                    CultureInfo.InvariantCulture,
                    "FS={0} \u00B7 SE={1} \u00B7 Mode={2}",
                    scene.FieldState,
                    scene.IsSimpleEventBlocking ? "Blk" : (scene.IsSimpleEventActive ? "Act" : "-"),
                    scene.CutsceneModeFlag ? "1" : "0");
            }
        }

        _lastOverlayVisible = true;
        _lastOverlayFfState = isFastForwarding;
        _lastOverlayKind = activeKind;

        _overlay.UpdateState(new OverlayState(
            Visible: true,
            IsFastForwarding: isFastForwarding,
            Speed: speed,
            ModeTag: modeTag,
            HintLabel: hint,
            DebugDetail: debugDetail,
            Corner: cfg.OverlayCorner,
            UserScale: cfg.GetClampedOverlayScale(),
            Opacity: cfg.GetClampedOpacity()));
    }

    private void HideOverlay()
    {
        if (!_lastOverlayVisible && !_lastOverlayFfState && _lastOverlayKind == ActiveSceneKind.None)
            return;

        _lastOverlayVisible = false;
        _lastOverlayFfState = false;
        _lastOverlayKind = ActiveSceneKind.None;
        _overlay?.UpdateState(default);
    }

    private static string BuildHintLabel(Config cfg)
    {
        string verb = cfg.ActivationMode == ActivationMode.Hold ? "Hold" : "Press";
        var keys = new List<string>(3);
        if (cfg.UseStandardSkipButton)
            keys.Add("Confirm");
        if (cfg.ExtraKeyboardKey != ExtraKeyboardKey.None)
            keys.Add(cfg.ExtraKeyboardKey.ToString());
        if (cfg.ExtraGamepadButton != ExtraGamepadButton.None)
            keys.Add(cfg.ExtraGamepadButton.ToString());

        string binding = keys.Count > 0 ? string.Join(" / ", keys) : "Unbound";
        return $"{verb} {binding}";
    }

    private void LogPointerChangesIfNeeded(nint framework, FrameClock* clock)
    {
        PointerDiagnostics diag = _gameState.ReadPointerDiagnostics(framework, _lastTaskGraphPtr);
        if (!_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            _lastPtrDiag = diag;
            string clockInfo = clock != null
                ? string.Format(CultureInfo.InvariantCulture, "Last={0}, Clamped={1}, Max={2}, Raw={3}, Scale={4:0.###}",
                    clock->LastTicks, clock->ClampedDeltaTicks, clock->MaxDeltaTicks, clock->RawDeltaTicks, clock->TimeScale)
                : "NULL";
            _log?.Debug(
                $"First MainFrameTick: Fw=0x{diag.FrameworkPtr:X}, Clock=0x{diag.FrameClockPtr:X} [{clockInfo}], " +
                $"TG=0x{diag.TaskGraphPtr:X}, EvMgr=0x{diag.EventMgrPtr:X}, SEMgr=0x{diag.SimpleEventMgrPtr:X}, " +
                $"Input=0x{diag.InputStatePtr:X}, ModeObj=0x{diag.CutsceneModeObjPtr:X}, MovieMgr=0x{diag.MovieMgrPtr:X}, " +
                $"FieldSt=0x{diag.FieldStatePtr:X}, CtrlLock=0x{diag.ControlLockPtr:X}");
            return;
        }

        if (diag != _lastPtrDiag)
        {
            _lastPtrDiag = diag;
            _log?.Debug(
                $"Engine pointers updated: Clock=0x{diag.FrameClockPtr:X}, TG=0x{diag.TaskGraphPtr:X}, EvMgr=0x{diag.EventMgrPtr:X}, " +
                $"SEMgr=0x{diag.SimpleEventMgrPtr:X}, Input=0x{diag.InputStatePtr:X}, ModeObj=0x{diag.CutsceneModeObjPtr:X}, " +
                $"MovieMgr=0x{diag.MovieMgrPtr:X}, FieldSt=0x{diag.FieldStatePtr:X}, CtrlLock=0x{diag.ControlLockPtr:X}");
        }
    }

    private void LogSceneTransitionIfNeeded(in SceneSnapshot scene, ActiveSceneKind activeKind)
    {
        if (_hasLoggedScene && scene.HasSameCoreStateAs(_lastLoggedScene))
            return;

        _hasLoggedScene = true;
        _lastLoggedScene = scene;

        string movieTag = scene.Movie.IsPlaying
            ? string.Format(CultureInfo.InvariantCulture, "Play(f={0}/{1}@{2:0.#})", scene.Movie.CurrentFrame, scene.Movie.TotalFrames, scene.Movie.Fps)
            : (scene.Movie.IsPaused ? $"Paused(st={scene.Movie.MovieState}/{scene.Movie.BinkPlayState})" : (scene.IsMovie ? "Init" : "None"));

        _log?.Debug(string.Format(
            CultureInfo.InvariantCulture,
            "Scene: Active={0} | Event={1}, Blk={2}, Id=0x{3:X} ({3}), t={4:0.00}s/{5:0.0}s, Sync=0x{6:X}, " +
            "SecEv={7}(0x{8:X}), Movie={9}, EvPaused={10}, TGPaused={11}(0x{12:X}), Flags=0x{13:X}, " +
            "ModeFlag={14}, SE={15}(Blk={16}), FieldSt={17}, Perm18={18}, CtrlLock=0x{19:X}",
            activeKind, scene.EventActive, scene.IsBlockingCutscene, scene.CutsceneId,
            scene.TimelineCurrentTime, scene.TimelineTotalSeconds, scene.TimelineSyncBlocker,
            scene.SecondaryEventCount, scene.SecondaryEventId, movieTag,
            scene.IsCutscenePaused, scene.IsTaskGraphEventPaused, scene.TaskGraphPauseMask,
            scene.RawEventFlags, scene.CutsceneModeFlag, scene.IsSimpleEventActive, scene.IsSimpleEventBlocking,
            scene.FieldState, scene.IsPerm18Allowed, scene.ControlLockMask));
    }

    private void TraceSceneInputsIfNeeded(in SceneSnapshot scene, ActiveSceneKind activeKind, Config cfg)
    {
        if (!cfg.DebugTraceSceneInputs)
            return;

        bool anyScene = scene.IsAnySceneActive && !scene.IsTaskGraphEventPaused;
        if (!anyScene)
        {
            if (_wasAnySceneActive)
            {
                _wasAnySceneActive = false;
                _input.ResetDebugTraceState();
            }
            return;
        }

        _wasAnySceneActive = true;
        string? pressed = _input.SampleNewlyPressedDebugInputs(_gameState.GetUnifiedInputSnapshot(), cfg.ExtraKeyboardKey);
        if (pressed != null)
        {
            _log?.Debug(string.Format(
                CultureInfo.InvariantCulture,
                "SceneInput: {0} | Active={1}, TGPaused={2}(0x{3:X}), t={4:0.00}s, Id=0x{5:X}, FieldSt={6}, Perm18={7}, CtrlLock=0x{8:X}",
                pressed, activeKind, scene.IsTaskGraphEventPaused, scene.TaskGraphPauseMask,
                scene.TimelineCurrentTime, scene.CutsceneId, scene.FieldState, scene.IsPerm18Allowed, scene.ControlLockMask));
        }
    }

    private void HandleHookException(string stage, Exception ex, FrameClock* clock)
    {
        _hookExceptionCount++;
        try
        {
            StopFastForwardIfNeeded(clock, default, $"exception in {stage}");
            HideOverlay();
        }
        catch
        {
            // Ignore secondary error while restoring.
        }

        _toggleActive = false;
        _wasInputHeld = false;

        if (_hookExceptionCount <= 5 || _hookExceptionCount % 300 == 0)
            _log?.Error($"Exception in MainFrameTick ({stage}, count={_hookExceptionCount})", ex);
    }

    public void Suspend()
    {
        _suspended = true;
        FrameClock* clock = _gameState.GetFrameClock(_lastFrameworkPtr);
        StopFastForwardIfNeeded(clock, default, "mod suspended");
        HideOverlay();
        _log?.Info("Mod suspended.");
    }

    public void Resume()
    {
        _suspended = false;
        _log?.Info("Mod resumed.");
    }

    public void Unload()
    {
        Disposing();
        _suspended = true;
        FrameClock* clock = _gameState.GetFrameClock(_lastFrameworkPtr);
        StopFastForwardIfNeeded(clock, default, "mod unloading");
        HideOverlay();
        _taskGraphExecuteHook?.Disable();
        _mainFrameTickHook?.Disable();
        _overlay?.Dispose();
        _watchedConfig?.Dispose();
        _log?.Dispose();
    }

    public bool CanUnload() => false;
    public bool CanSuspend() => true;
}
