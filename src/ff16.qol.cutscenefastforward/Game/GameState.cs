using System.Runtime.InteropServices;

namespace ff16.qol.cutscenefastforward.Game;

/// <summary>
/// Faith Engine global frame clock located at *(FrameClock**)(g_Framework + 0x5B8).
/// Note: TaskGraph::ExecuteFrame (0x813EAC) overrides per-task TimeScale using TimeScaleChannels[0..7].
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct FrameClock
{
    [FieldOffset(0x00)] public long LastTicks;
    [FieldOffset(0x08)] public long ClampedDeltaTicks;
    [FieldOffset(0x10)] public long MaxDeltaTicks;
    [FieldOffset(0x18)] public long RawDeltaTicks;
    [FieldOffset(0x20)] public double TimeScale;
}

internal enum ActiveSceneKind
{
    None,
    Cutscene,
    Movie,
    SideScene,
    SimpleEvent
}

internal readonly record struct MoviePlaybackInfo(
    bool IsPlaying,
    bool IsPaused,
    nint MovieInstancePtr,
    nint BinkPlayerPtr,
    byte MovieState,
    int BinkPlayState,
    uint CurrentMs,
    uint CurrentFrame,
    uint TotalFrames,
    uint FrameRate,
    uint FrameRateDiv)
{
    public double Fps => FrameRateDiv > 0 ? (double)FrameRate / FrameRateDiv : 0.0;
    public double CurrentSeconds => CurrentMs / 1000.0;
    public double TotalSeconds => Fps > 0.01 ? TotalFrames / Fps : 0.0;
}

internal readonly record struct SceneSnapshot(
    bool EventActive,
    bool IsBlockingCutscene,
    bool IsMovie,
    bool IsCutscenePaused,
    bool CanPause,
    bool CutsceneModeFlag,
    bool IsSimpleEventActive,
    bool IsSimpleEventBlocking,
    int SecondaryEventCount,
    uint SecondaryEventId,
    uint CutsceneId,
    uint RawEventFlags,
    float EventPlaybackSpeed,
    float TimelineCurrentTime,
    int TimelineEndFrame,
    nint TimelineSyncBlocker,
    uint TimelineFlags,
    uint TaskGraphPauseMask,
    bool IsTaskGraphEventPaused,
    uint FieldState,
    ulong ControlLockMask,
    bool IsPerm18Allowed,
    MoviePlaybackInfo Movie)
{
    /// <summary>
    /// Pause bitmask registered for EventManager::Update (task 0xB2 @ ffxvi.exe+0x80A3B7 -> 495 = 0x1EF).
    /// When any of these bits is set in TaskGraph+0x517C (and override counter +0x5180 &lt;= 0),
    /// cutscenes are paused (Cutscene Pause Menu, System Pause, Active Time Lore).
    /// </summary>
    public const uint EventManagerPauseBitmask = 0x1EFu;

    public bool IsAnyPauseActive => IsCutscenePaused || IsTaskGraphEventPaused;

    /// <summary>
    /// True whenever a pre-rendered Bink2 (.bk2) movie is attached to the active Event or loaded in MovieManager.
    /// Prevents TaskGraph scaling from interfering with Bink2 movie pacing.
    /// </summary>
    public bool IsAnyMovieActive => IsMovie || Movie.IsPlaying || Movie.IsPaused;

    public float TimelineTotalSeconds => TimelineEndFrame > 0 ? TimelineEndFrame / 30.0f : 0.0f;

    public bool IsEligibleInEngineCutscene =>
        EventActive && IsBlockingCutscene && !IsAnyMovieActive && !IsAnyPauseActive;

    public bool IsEligibleMovie =>
        Movie.IsPlaying && !IsAnyPauseActive;

    public bool IsEligibleSideScene =>
        !IsAnyMovieActive &&
        !IsAnyPauseActive &&
        !IsEligibleInEngineCutscene &&
        !IsSimpleEventActive &&
        (
            CutsceneModeFlag ||
            (((EventActive && !IsBlockingCutscene) || SecondaryEventCount > 0) && !IsPerm18Allowed)
        );

    public bool IsEligibleSimpleEvent =>
        IsSimpleEventActive && !IsAnyPauseActive && !IsAnyMovieActive;

    public bool IsAnySceneActive =>
        EventActive || SecondaryEventCount > 0 || CutsceneModeFlag || IsSimpleEventActive || IsAnyMovieActive;

    public ActiveSceneKind ResolveActiveKind(
        bool allowCutscenes,
        bool allowMovies,
        bool allowSideScenes,
        bool allowSimpleEvents)
    {
        if (allowMovies && IsEligibleMovie)
            return ActiveSceneKind.Movie;
        if (allowCutscenes && IsEligibleInEngineCutscene)
            return ActiveSceneKind.Cutscene;
        if (allowSideScenes && IsEligibleSideScene)
            return ActiveSceneKind.SideScene;
        if (allowSimpleEvents && IsEligibleSimpleEvent)
            return ActiveSceneKind.SimpleEvent;
        return ActiveSceneKind.None;
    }

    public bool HasSameCoreStateAs(in SceneSnapshot other) =>
        EventActive == other.EventActive &&
        IsBlockingCutscene == other.IsBlockingCutscene &&
        IsMovie == other.IsMovie &&
        Movie.IsPlaying == other.Movie.IsPlaying &&
        Movie.IsPaused == other.Movie.IsPaused &&
        Movie.MovieState == other.Movie.MovieState &&
        Movie.BinkPlayState == other.Movie.BinkPlayState &&
        IsCutscenePaused == other.IsCutscenePaused &&
        IsTaskGraphEventPaused == other.IsTaskGraphEventPaused &&
        TaskGraphPauseMask == other.TaskGraphPauseMask &&
        CanPause == other.CanPause &&
        CutsceneModeFlag == other.CutsceneModeFlag &&
        IsSimpleEventActive == other.IsSimpleEventActive &&
        IsSimpleEventBlocking == other.IsSimpleEventBlocking &&
        SecondaryEventCount == other.SecondaryEventCount &&
        SecondaryEventId == other.SecondaryEventId &&
        CutsceneId == other.CutsceneId &&
        (RawEventFlags & ~0x4u) == (other.RawEventFlags & ~0x4u) &&
        FieldState == other.FieldState &&
        IsPerm18Allowed == other.IsPerm18Allowed &&
        (TimelineSyncBlocker != 0) == (other.TimelineSyncBlocker != 0);
}

internal readonly record struct PointerDiagnostics(
    nint FrameworkPtr,
    nint FrameClockPtr,
    nint TaskGraphPtr,
    nint EventMgrPtr,
    nint SimpleEventMgrPtr,
    nint InputStatePtr,
    nint CutsceneModeObjPtr,
    nint MovieMgrPtr,
    nint FieldStatePtr,
    nint ControlLockPtr);

internal unsafe class GameStateReader
{
    // TaskGraph offsets verified in ffxvi.exe+0x813EAC (TaskGraph::ExecuteFrame)
    // and ffxvi.exe+0x813D04 (TimeController::Update @ taskGraph + 0x5118, Task 0x8D).
    // TimeController double-buffers channels: Task 0x8D copies Pending (+0x10..+0x2C) -> Active (+0x44..+0x60)
    // right before EventManager::Update (Task 0xB2) executes.
    public const int TaskGraphPendingChannel0Offset = 0x5128;
    public const int TaskGraphPendingChannel1Offset = 0x512C;
    public const int TaskGraphPendingFixedDtValOffset = 0x5150;
    public const int TaskGraphChannel0Offset = 0x515C;
    public const int TaskGraphChannel1Offset = 0x5160;
    public const int TaskGraphPauseMaskOffset = 0x517C;
    public const int TaskGraphPauseOverrideOffset = 0x5180;
    public const int TaskGraphFixedDtValOffset = 0x5184;
    public const int TaskGraphFixedDtFlagOffset = 0x5188;

    // Addresses resolved via SigScan (pointers to globals holding object pointers)
    public nint EventMgrGlobalAddr;
    public nint SimpleEventMgrGlobalAddr;
    public nint InputStateGlobalAddr;
    public nint CutsceneModeGlobalAddr;
    public nint MovieMgrGlobalAddr;
    public nint FieldStateGlobalAddr;
    public nint ControlLockGlobalAddr;

    // Native engine function pointers
    public delegate* unmanaged[Stdcall]<nint, byte> EventMgrIsActiveFn;
    public delegate* unmanaged[Stdcall]<nint, byte> EventIsBlockingCutsceneFn;
    public delegate* unmanaged[Stdcall]<nint, byte> EventCanPauseFn;

    public bool IsReady =>
        EventMgrGlobalAddr != 0 &&
        EventMgrIsActiveFn != null &&
        EventIsBlockingCutsceneFn != null;

    public FrameClock* GetFrameClock(nint frameworkPtr)
    {
        if (frameworkPtr == 0)
            return null;
        return *(FrameClock**)(frameworkPtr + 0x5B8);
    }

    public byte* GetUnifiedInputSnapshot()
    {
        if (InputStateGlobalAddr == 0)
            return null;
        nint inputState = *(nint*)InputStateGlobalAddr;
        if (inputState == 0)
            return null;
        return (byte*)(inputState + 0x08);
    }

    public static bool IsTaskGraphEventPaused(nint taskGraphPtr, out uint pauseMask)
    {
        pauseMask = 0;
        if (taskGraphPtr == 0)
            return false;

        int pauseOverride = *(int*)(taskGraphPtr + TaskGraphPauseOverrideOffset);
        pauseMask = *(uint*)(taskGraphPtr + TaskGraphPauseMaskOffset);
        return pauseOverride <= 0 && (pauseMask & SceneSnapshot.EventManagerPauseBitmask) != 0;
    }

    /// <summary>
    /// Advances a playing Bink2 movie's virtual start timestamp earlier by up to requestedDeltaMs.
    /// Bink2's worker thread (ffxvi.exe+0xA4A759) computes elapsedMs = timeGetTime() - *(uint*)(binkPlayer + 0x120)
    /// and catches up via BinkDoFrameAsyncMulti. Event::Update stays automatically synced to movie playback time.
    /// </summary>
    public static int AdvanceBinkMovieStartTime(in MoviePlaybackInfo movie, int requestedDeltaMs, out bool backpressured)
    {
        backpressured = false;
        if (!movie.IsPlaying || movie.BinkPlayerPtr == 0 || requestedDeltaMs <= 0)
            return 0;

        // Leave the final 2 frames unshifted so Bink2 finishes and triggers scene-end cleanly.
        if (movie.TotalFrames <= 2 || movie.CurrentFrame >= movie.TotalFrames - 2)
            return 0;

        double fps = movie.Fps;
        if (!(fps > 1.0 && fps <= 240.0))
            return 0;

        // Re-verify live Bink play state (3 = Playing) right before shifting startTimeMs.
        nint syncObj = *(nint*)(movie.BinkPlayerPtr + 0x08);
        if (!IsLikelyValidPointer(syncObj) || *(int*)(syncObj + 0x08) != 3)
            return 0;

        uint currentMs = *(uint*)(movie.BinkPlayerPtr + 0x130);
        double expectedFrame = (currentMs / 1000.0) * fps;

        // Backpressure guard: if Bink2's multi-threaded decoder is already >4 frames behind
        // the virtual clock, hold off so releasing the button stops fast-forward instantaneously.
        if (expectedFrame > movie.CurrentFrame + 4.0)
        {
            backpressured = true;
            return 0;
        }

        double headFrame = Math.Max(expectedFrame, movie.CurrentFrame);
        double remainingFrames = (movie.TotalFrames - 2) - headFrame;
        if (remainingFrames <= 0.5)
            return 0;

        int maxAdvanceMs = (int)Math.Floor((remainingFrames / fps) * 1000.0);
        if (maxAdvanceMs <= 0)
            return 0;

        int applyMs = Math.Min(requestedDeltaMs, Math.Min(maxAdvanceMs, 250));
        if (applyMs <= 0)
            return 0;

        Interlocked.Add(ref *(int*)(movie.BinkPlayerPtr + 0x120), -applyMs);
        return applyMs;
    }

    public PointerDiagnostics ReadPointerDiagnostics(nint frameworkPtr, nint taskGraphPtr)
    {
        nint clockPtr = frameworkPtr != 0 ? (nint)(*(FrameClock**)(frameworkPtr + 0x5B8)) : 0;
        nint eventMgr = EventMgrGlobalAddr != 0 ? *(nint*)EventMgrGlobalAddr : 0;
        nint simpleEventMgr = SimpleEventMgrGlobalAddr != 0 ? *(nint*)SimpleEventMgrGlobalAddr : 0;
        nint inputState = InputStateGlobalAddr != 0 ? *(nint*)InputStateGlobalAddr : 0;
        nint modeObj = CutsceneModeGlobalAddr != 0 ? *(nint*)CutsceneModeGlobalAddr : 0;
        nint movieMgr = MovieMgrGlobalAddr != 0 ? *(nint*)MovieMgrGlobalAddr : 0;
        nint fieldState = FieldStateGlobalAddr != 0 ? *(nint*)FieldStateGlobalAddr : 0;
        nint controlLock = ControlLockGlobalAddr != 0 ? *(nint*)ControlLockGlobalAddr : 0;

        return new PointerDiagnostics(
            FrameworkPtr: frameworkPtr,
            FrameClockPtr: clockPtr,
            TaskGraphPtr: taskGraphPtr,
            EventMgrPtr: eventMgr,
            SimpleEventMgrPtr: simpleEventMgr,
            InputStatePtr: inputState,
            CutsceneModeObjPtr: modeObj,
            MovieMgrPtr: movieMgr,
            FieldStatePtr: fieldState,
            ControlLockPtr: controlLock);
    }

    public SceneSnapshot ReadSnapshot(nint taskGraphPtr)
    {
        bool eventActive = false;
        bool isBlocking = false;
        bool isMovie = false;
        bool isPaused = false;
        bool canPause = false;
        int secondaryEventCount = 0;
        uint secondaryEventId = 0;
        uint cutsceneId = 0;
        uint eventFlags = 0;
        float playbackSpeed = 0f;
        float timelineTime = 0f;
        int timelineEndFrame = 0;
        nint timelineSyncBlocker = 0;
        uint timelineFlags = 0;

        if (EventMgrGlobalAddr != 0)
        {
            nint eventMgr = *(nint*)EventMgrGlobalAddr;
            if (eventMgr != 0)
            {
                nint activeEvent = *(nint*)(eventMgr + 0x408);
                if (activeEvent != 0)
                {
                    eventActive = EventMgrIsActiveFn != null && EventMgrIsActiveFn(eventMgr) != 0;
                    cutsceneId = *(uint*)(activeEvent + 0xB0);
                    isMovie = *(nint*)(activeEvent + 0x3F8) != 0;
                    playbackSpeed = *(float*)(activeEvent + 0x384);

                    // Embedded TimelineCore @ activeEvent + 0xE8
                    timelineTime = *(float*)(activeEvent + 0xF4);
                    timelineFlags = *(uint*)(activeEvent + 0x110);
                    timelineSyncBlocker = *(nint*)(activeEvent + 0x118);
                    timelineEndFrame = *(int*)(activeEvent + 0x120);

                    // Pause flags set by Event::PauseCutscene (0x646344: or [ev+0x408], 0x4800)
                    eventFlags = *(uint*)(activeEvent + 0x408);
                    isPaused = (eventFlags & 0x4800u) != 0;

                    if (eventActive)
                    {
                        isBlocking = EventIsBlockingCutsceneFn != null && EventIsBlockingCutsceneFn(activeEvent) != 0;
                        canPause = EventCanPauseFn != null && EventCanPauseFn(activeEvent) != 0;
                    }
                }

                // Walk EventManager active event list (+0x3E0, intrusive link at Event+0x38, sentinel at eventMgr+0x3A8)
                nint sentinel = eventMgr + 0x3A8;
                nint node = *(nint*)(eventMgr + 0x3E0);
                for (int i = 0; i < 16 && node != 0; i++)
                {
                    nint ev = node - 0x38;
                    if (ev == sentinel)
                        break;

                    nint nextNode = *(nint*)(ev + 0x38);
                    if (ev != activeEvent)
                    {
                        nint vtable = *(nint*)ev;
                        if (vtable != 0)
                        {
                            var isEvActiveFn = *(delegate* unmanaged[Stdcall]<nint, byte>*)(vtable + 0x50);
                            if (isEvActiveFn != null && isEvActiveFn(ev) != 0)
                            {
                                secondaryEventCount++;
                                if (secondaryEventId == 0)
                                    secondaryEventId = *(uint*)(ev + 0xB0);
                            }
                        }
                    }

                    node = nextNode;
                }
            }
        }

        bool cutsceneModeFlag = false;
        if (CutsceneModeGlobalAddr != 0)
        {
            nint modeObj = *(nint*)CutsceneModeGlobalAddr;
            if (modeObj != 0)
                cutsceneModeFlag = *(byte*)(modeObj + 0x128E) != 0;
        }

        bool simpleEventActive = false;
        bool simpleEventBlocking = false;
        if (SimpleEventMgrGlobalAddr != 0)
        {
            nint seMgr = *(nint*)SimpleEventMgrGlobalAddr;
            if (seMgr != 0)
            {
                nint activeSe = *(nint*)(seMgr + 0xE8);
                if (activeSe != 0)
                {
                    nint vtable = *(nint*)activeSe;
                    if (vtable != 0)
                    {
                        var isActiveFn = *(delegate* unmanaged[Stdcall]<nint, byte>*)(vtable + 0x80);
                        if (isActiveFn != null && isActiveFn(activeSe) != 0)
                        {
                            simpleEventActive = true;
                            var isBlockingFn = *(delegate* unmanaged[Stdcall]<nint, byte>*)(vtable + 0x38);
                            if (isBlockingFn != null)
                                simpleEventBlocking = isBlockingFn(activeSe) != 0;
                        }
                    }
                }
            }
        }

        uint fieldState = 0;
        if (FieldStateGlobalAddr != 0)
        {
            nint fieldObj = *(nint*)FieldStateGlobalAddr;
            if (fieldObj != 0)
                fieldState = *(uint*)(fieldObj + 0x28);
        }

        ulong controlLockMask = 0;
        bool isPerm18Allowed = true;
        if (ControlLockGlobalAddr != 0)
        {
            nint lockObj = *(nint*)ControlLockGlobalAddr;
            if (lockObj != 0)
            {
                controlLockMask = *(ulong*)(lockObj + 0x10);
                ulong perm18Mask = *(ulong*)(lockObj + (0x18 * 8) + 0x18);
                isPerm18Allowed = (controlLockMask & perm18Mask) == 0;
            }
        }

        MoviePlaybackInfo movieInfo = ReadMovieState();
        bool tgPaused = IsTaskGraphEventPaused(taskGraphPtr, out uint tgPauseMask);

        return new SceneSnapshot(
            EventActive: eventActive,
            IsBlockingCutscene: isBlocking,
            IsMovie: isMovie,
            IsCutscenePaused: isPaused,
            CanPause: canPause,
            CutsceneModeFlag: cutsceneModeFlag,
            IsSimpleEventActive: simpleEventActive,
            IsSimpleEventBlocking: simpleEventBlocking,
            SecondaryEventCount: secondaryEventCount,
            SecondaryEventId: secondaryEventId,
            CutsceneId: cutsceneId,
            RawEventFlags: eventFlags,
            EventPlaybackSpeed: playbackSpeed,
            TimelineCurrentTime: timelineTime,
            TimelineEndFrame: timelineEndFrame,
            TimelineSyncBlocker: timelineSyncBlocker,
            TimelineFlags: timelineFlags,
            TaskGraphPauseMask: tgPauseMask,
            IsTaskGraphEventPaused: tgPaused,
            FieldState: fieldState,
            ControlLockMask: controlLockMask,
            IsPerm18Allowed: isPerm18Allowed,
            Movie: movieInfo);
    }

    private static bool IsLikelyValidPointer(nint ptr) => (nuint)ptr >= 0x10000 && (nuint)ptr < 0x00008000_00000000;

    private MoviePlaybackInfo ReadMovieState()
    {
        if (MovieMgrGlobalAddr == 0)
            return default;

        nint movieMgr = *(nint*)MovieMgrGlobalAddr;
        if (!IsLikelyValidPointer(movieMgr))
            return default;

        nint begin = *(nint*)(movieMgr + 0x38);
        nint end = *(nint*)(movieMgr + 0x40);
        if (!IsLikelyValidPointer(begin) || end <= begin)
            return default;

        long count = (end - begin) / sizeof(nint);
        if (count <= 0 || count > 64)
            return default;

        MoviePlaybackInfo pausedFallback = default;

        for (long i = 0; i < count; i++)
        {
            nint movie = *(nint*)(begin + (nint)(i * sizeof(nint)));
            if (!IsLikelyValidPointer(movie))
                continue;

            byte movieState = *(byte*)(movie + 0x1C4); // 3 = Playing, 4 = Paused
            if (movieState != 3 && movieState != 4)
                continue;

            // Verified pointer chain in ffxvi.exe v1.03:
            //   0xC03918: movie (+0x00) -> inner
            //   0xC0391B: inner (+0x20) -> outerProxy (MoviePlayerProxy)
            //   0xA4B9C4: outerProxy (+0x10) -> binkWrapper (vtable 0x16400C8)
            //   0xA4B76C: binkWrapper (+0x10) -> binkPlayer
            //   0xA4B772: binkPlayer (+0x08) -> syncObj; syncObj (+0x08) -> int binkPlayState (3 = Playing)
            //   0xA4A72C: binkPlayer (+0x10) -> HBINK; +0x120 -> uint startTimeMs; +0x130 -> uint currentMs
            nint inner = *(nint*)(movie + 0x00);
            if (!IsLikelyValidPointer(inner))
                continue;

            nint outerProxy = *(nint*)(inner + 0x20);
            if (!IsLikelyValidPointer(outerProxy))
                continue;

            nint binkWrapper = *(nint*)(outerProxy + 0x10);
            if (!IsLikelyValidPointer(binkWrapper))
                continue;

            nint binkPlayer = *(nint*)(binkWrapper + 0x10);
            if (!IsLikelyValidPointer(binkPlayer))
                continue;

            nint syncObj = *(nint*)(binkPlayer + 0x08);
            if (!IsLikelyValidPointer(syncObj))
                continue;

            int binkPlayState = *(int*)(syncObj + 0x08); // 3 = Playing, 2 = Ready/Paused
            nint hbink = *(nint*)(binkPlayer + 0x10);
            if (!IsLikelyValidPointer(hbink))
                continue;

            uint totalFrames = *(uint*)(hbink + 0x08);
            uint currentFrame = *(uint*)(hbink + 0x0C);
            uint frameRate = *(uint*)(hbink + 0x14);
            uint frameRateDiv = *(uint*)(hbink + 0x18);
            if (totalFrames == 0 || totalFrames > 1_000_000 || frameRate == 0 || frameRateDiv == 0 || frameRate > 1_000_000)
                continue;

            uint currentMs = *(uint*)(binkPlayer + 0x130);
            bool playing = movieState == 3 && binkPlayState == 3;
            bool paused = movieState == 4 || (movieState == 3 && binkPlayState == 2);

            var info = new MoviePlaybackInfo(
                IsPlaying: playing,
                IsPaused: paused,
                MovieInstancePtr: movie,
                BinkPlayerPtr: binkPlayer,
                MovieState: movieState,
                BinkPlayState: binkPlayState,
                CurrentMs: currentMs,
                CurrentFrame: currentFrame,
                TotalFrames: totalFrames,
                FrameRate: frameRate,
                FrameRateDiv: frameRateDiv);

            if (playing)
                return info;

            if (paused && !pausedFallback.IsPaused)
                pausedFallback = info;
        }

        return pausedFallback;
    }
}
