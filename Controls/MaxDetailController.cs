using System.Diagnostics;

namespace ForzavistaFreeRoam;

internal sealed record NativeMaxDetailStatus(bool On, bool Available, string Message,
    bool Requested = false, bool BuiltIn = false);
internal sealed record MaxDetailResult(bool Success, bool On, string Message);
internal readonly record struct RenderModeIdentity(int ProcessId, long StartTicks, ulong Module, ulong Controller);
internal readonly record struct RenderModeState(int Current, int Previous, int Option, byte Pending);
internal enum MaxDetailSceneKind { Other, Empty, Photo, Garage }
internal readonly record struct MaxDetailSceneState(ulong Root, ulong Table, ulong Scene, ulong Vtable,
    MaxDetailSceneKind Kind, int SavedReturnMode = 0, int Phase = 0)
{
    internal bool SameScene(MaxDetailSceneState other) => Root == other.Root && Table == other.Table &&
        Scene == other.Scene && Vtable == other.Vtable && Kind == other.Kind;
}
internal sealed record MaxDetailSceneMap(ulong RootGlobalRva, ulong RootVtableRva, ulong PhotoVtableRva,
    ulong PhotoSecondaryVtableRva, ulong EmptyVtableRva, ulong GarageVtableRva = 0, ulong GarageMenuVtableRva = 0)
{
    // Verified from this build's native PhotoModeScene constructor/entry/exit,
    // and user-labelled live Photo Mode -> Free Roam snapshots.
    internal static MaxDetailSceneMap Steam461 => new(0xA8DB8A8, 0x6C3A028, 0x70CC6D0, 0x70CCC48, 0x708C168);
    // Independent RTTI/native code matches and user-labelled Xbox snapshots.
    internal static MaxDetailSceneMap Xbox461 => new(0xA8D3CE8, 0x6C26D28, 0x70BAD30,
        0x70BB2A8, 0x707A838, 0x6FF4820, 0x7052BA0);
}
internal readonly record struct MaxDetailModeMap(int Photo, int FreeRoam, int Garage, int Detail)
{
    // Keep older profiles unchanged until their scene mappings are checked.
    internal static MaxDetailModeMap Legacy => new(6, 9, -1, 8);
    // User-labelled read-only baselines on Steam 6.461.691.0: Photo=6,
    // garage=7, Free Roam=9. Do not infer runtime IDs from XML list order.
    internal static MaxDetailModeMap Steam461 => new(6, 9, 7, 8);
    // Xbox live garage menu and walk-around both use 13; Photo=6, Free Roam=9.
    internal static MaxDetailModeMap Xbox461 => new(6, 9, 13, 8);
    internal bool CanEnable(RenderModeState state) => state.Pending == 0 && state.Option == 0 &&
        (state.Current == Photo || state.Current == FreeRoam || (Garage >= 0 && state.Current == Garage));
    internal bool ProvidesDetail(RenderModeState state) => state.Pending == 0 && state.Option == 0 &&
        state.Current == Detail;
    internal int DetailFor(int original) => Detail;
}

internal interface IMaxDetailRenderSession : IDisposable
{
    RenderModeIdentity Identity { get; }
    MaxDetailModeMap Modes => MaxDetailModeMap.Legacy;
    RenderModeState ReadState();
    MaxDetailSceneState? ReadScene() => null;
    void RepairPhotoReturn(MaxDetailSceneState expected, int mode) => throw new NotSupportedException();
    void Publish(RenderModeState expected, int mode, int option, MaxDetailSceneState? expectedScene = null);
}

// The same ownership/transition logic is exercised by offline fake sessions.
// Reading status never publishes a request or restores an earlier scene.
internal sealed class MaxDetailController(Func<bool, IMaxDetailRenderSession?> openSession,
    TimeSpan? confirmationTimeout = null)
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout = confirmationTimeout ?? TimeSpan.FromMilliseconds(1500);
    private Ownership? _owned;
    // A game-driven scene change can release ownership without turning the
    // user's next OFF click into a new ON request. Polling still never writes.
    private RenderModeIdentity? _requestedIdentity;
    // Photo Mode caches the current renderer on entry, independently of its
    // active renderer. Keep the normal origin until that cache is repaired or
    // its exit has been observed; clearing only _owned loses this obligation.
    private ReturnOrigin? _returnOrigin;
    private sealed record ReturnOrigin(RenderModeIdentity Identity, int Mode, int Option,
        MaxDetailSceneState? Photo = null, bool OwnsCache = false);

    private sealed record Ownership(RenderModeIdentity Identity, int OriginalMode, int OriginalOption,
        int DetailMode, int RequestedMode, int ExpectedPrevious, bool PublicationFailed = false,
        bool HistoryChanged = false, MaxDetailSceneState? Scene = null)
    {
        internal bool Matches(RenderModeState state) => state.Current == RequestedMode &&
            state.Previous == ExpectedPrevious && state.Option == (RequestedMode == OriginalMode ? OriginalOption : 0);
    }

    internal NativeMaxDetailStatus GetStatus()
    {
        lock (_gate)
        {
            using var session = openSession(false);
            if (session is null)
            {
                _owned = null;
                _requestedIdentity = null;
                _returnOrigin = null;
                return new(false, false, "MAX DETAIL requires a supported game with a loaded render view.");
            }
            var state = session.ReadState();
            var scene = session.ReadScene();
            Observe(session, state, scene);
            var canEnable = session.Modes.CanEnable(state) && scene is not { Kind: MaxDetailSceneKind.Photo, Phase: not 0 };
            var requested = _requestedIdentity is not null;
            var builtIn = _owned is null && _requestedIdentity is null && session.Modes.ProvidesDetail(state);
            return new(_owned is not null, requested || canEnable || builtIn,
                _owned is { PublicationFailed: true } ? "MAX DETAIL update was not verified; RESET STATE retries owned cleanup."
                : _owned is { HistoryChanged: true } ? "The game changed scene history while full detail remains. OFF will not overwrite an unverified scene."
                : _owned is not null && state.Pending != 0 ? "MAX DETAIL transition is still pending; wait before OFF or RESET STATE."
                : _owned is not null ? "MAX DETAIL is owned by this tool; OFF, RESET STATE and closing restore the original mode."
                : _returnOrigin is { OwnsCache: true } ? "Photo Mode saved the MAX DETAIL override. RESET restores its normal return view before exit."
                : _returnOrigin?.Photo is not null ? "The Photo Mode return view is tracked. RESET restores an owned override in the current view."
                : requested ? "The game changed views. Press RESET to clear the earlier MAX DETAIL request without re-enabling it."
                : builtIn ? "The game already supplies full car detail in this view; no override is needed."
                : canEnable ? "MAX DETAIL captures and restores this view (Free Roam, Photo Mode or a mapped garage)."
                : "MAX DETAIL requires a mapped view with no scene transition pending.", requested, builtIn);
        }
    }

    internal MaxDetailResult SetEnabled(bool on)
    {
        lock (_gate)
        {
            if (!on) return RestoreCore();
            using var session = openSession(true);
            if (session is null) return new(false, false, "Game not running; MAX DETAIL was not changed.");
            var state = session.ReadState();
            var scene = session.ReadScene();
            Observe(session, state, scene);
            if (_requestedIdentity is not null)
                return new(false, true, "MAX DETAIL is already tracked; use OFF or RESET STATE first.");
            if (session.Modes.ProvidesDetail(state))
                return new(true, false, "This view already has full car detail from the game; no render mode was changed.");
            if (!session.Modes.CanEnable(state))
                return new(false, false, "MAX DETAIL requires a mapped view. Wait for loading to finish.");
            if (scene is { Kind: MaxDetailSceneKind.Photo, Phase: not 0 })
                return new(false, false, "Return to the normal Photo Mode camera before enabling MAX DETAIL.");
            var detailMode = session.Modes.DetailFor(state.Current);
            _requestedIdentity = session.Identity;
            _owned = new(session.Identity, state.Current, state.Option, detailMode, detailMode, state.Current, Scene: scene);
            return PublishAndConfirm(session, state, detailMode, 0);
        }
    }

    internal NativeMaxDetailStatus GetUnavailableStatus(string message)
    {
        lock (_gate)
            return new(_owned is not null, _requestedIdentity is not null, message,
                _requestedIdentity is not null);
    }

    internal MaxDetailResult Restore()
    {
        lock (_gate) return RestoreCore();
    }

    private MaxDetailResult RestoreCore()
    {
        if (_owned is null && _returnOrigin is null)
        {
            _requestedIdentity = null;
            _returnOrigin = null;
            return new(true, false, "MAX DETAIL request cleared; no owned render mode to restore.");
        }
        using var session = openSession(true);
        if (session is null)
        {
            _owned = null;
            _requestedIdentity = null;
            _returnOrigin = null;
            return new(true, false, "Previous MAX DETAIL game session ended; no scene written.");
        }
        var state = session.ReadState();
        var scene = session.ReadScene();
        Observe(session, state, scene);
        if (_returnOrigin is { Photo: { } photo } origin && scene is { } active && photo.SameScene(active))
        {
            if (active.Phase != 0 || state.Pending != 0 || state.Option != 0)
                return new(false, _owned is not null, "Photo Mode is entering or exiting; wait, then use OFF or RESET STATE to restore its saved return view.");
            if (origin.OwnsCache)
            {
                try
                {
                    // This field is consumed by the game's exit path. Repair it
                    // while the same active Photo Mode scene still owns it.
                    session.RepairPhotoReturn(active, origin.Mode);
                    _returnOrigin = null;
                    scene = session.ReadScene();
                    Observe(session, state, scene);
                }
                catch (Exception error)
                {
                    return new(false, _owned is not null, "Photo Mode return cleanup not verified: " + error.GetBaseException().Message);
                }
            }
            else if (_owned is null && state.Current == session.Modes.Photo && active.SavedReturnMode == origin.Mode)
                return SceneChanged(); // Both active Photo and its normal return are already native.
        }
        if (_owned is null && _returnOrigin is { Photo: { } destinationPhoto } destination &&
            scene is { Kind: MaxDetailSceneKind.Empty or MaxDetailSceneKind.Garage } normal && normal.Root == destinationPhoto.Root &&
            normal.Table == destinationPhoto.Table && state == new RenderModeState(destination.Mode, state.Previous, destination.Option, 0))
            return SceneChanged(); // Confirmed native destination; no renderer write needed.
        if (_owned is null && _returnOrigin is not null)
            return new(false, false, "The saved MAX DETAIL return view is still tracked. Wait for Photo Mode or Free Roam to finish loading, then use OFF or RESET STATE.");
        if (_owned is null) return SceneChanged();
        if (_owned.HistoryChanged)
            return new(false, true, "MAX DETAIL cleanup cannot verify this changed scene. No scene was overwritten; return to normal Free Roam or Photo Mode, then use OFF or RESET STATE.");
        if (_owned.PublicationFailed)
        {
            // Retry only a fully recognizable owned state after a failed write.
            // A partial/unrecognized request is never guessed or overwritten.
            if (state.Current == _owned.DetailMode && state.Previous == _owned.OriginalMode && state.Option == 0)
                _owned = _owned with { RequestedMode = _owned.DetailMode, ExpectedPrevious = state.Previous, PublicationFailed = false };
            else return new(false, true, "MAX DETAIL cleanup cannot verify the incomplete render request; no further scene writes.");
        }
        if (state.Pending != 0)
        {
            var settled = Confirm(session);
            if (!settled.Success || _owned is null) return settled;
            state = session.ReadState();
            Observe(session, state, session.ReadScene());
            if (_owned is null) return SceneChanged();
        }
        if (_owned.RequestedMode == _owned.OriginalMode)
        {
            _owned = null;
            _requestedIdentity = null;
            _returnOrigin = null;
            return new(true, false, "Original MAX DETAIL render mode verified restored.");
        }
        return PublishAndConfirm(session, state, _owned.OriginalMode, _owned.OriginalOption);
    }

    private MaxDetailResult PublishAndConfirm(IMaxDetailRenderSession session, RenderModeState state,
        int requestedMode, int option)
    {
        _owned = _owned! with { RequestedMode = requestedMode, ExpectedPrevious = state.Current };
        try
        {
            session.Publish(state, requestedMode, option, _owned.Scene);
            // An attempted request is not evidence of a cached override. Only
            // retain a normal origin after our renderer request was published.
            if (requestedMode == _owned.DetailMode && _owned.Scene is { Kind: MaxDetailSceneKind.Empty or MaxDetailSceneKind.Garage } &&
                (_owned.OriginalMode == session.Modes.FreeRoam || _owned.OriginalMode == session.Modes.Garage))
                _returnOrigin = new(session.Identity, _owned.OriginalMode, _owned.OriginalOption);
            else if (requestedMode == _owned.DetailMode && _owned.Scene is { Kind: MaxDetailSceneKind.Photo, Phase: 0 } photo &&
                (photo.SavedReturnMode == session.Modes.FreeRoam || photo.SavedReturnMode == session.Modes.Garage))
                // A Photo-only override also needs its real exit destination.
                // Native exit can briefly restore that mode before replaying 8.
                _returnOrigin = new(session.Identity, photo.SavedReturnMode, 0, photo);
            return Confirm(session);
        }
        catch (Exception error)
        {
            _owned = _owned! with { PublicationFailed = true };
            return new(false, true, "MAX DETAIL update not verified: " + error.GetBaseException().Message);
        }
    }

    private MaxDetailResult Confirm(IMaxDetailRenderSession session)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            var state = session.ReadState();
            var scene = session.ReadScene();
            if (_owned is { } restored && restored.Identity == session.Identity &&
                restored.Matches(state) && restored.RequestedMode == restored.OriginalMode && state.Pending == 0 &&
                (restored.Scene is null || scene is { } active && restored.Scene.Value.SameScene(active)))
            {
                _owned = null;
                _requestedIdentity = null;
                _returnOrigin = null;
                return new(true, false, "Original MAX DETAIL render mode verified restored.");
            }
            Observe(session, state, scene);
            if (_owned is null)
                return _requestedIdentity is null ? SceneChanged() :
                    new(false, true, "The game changed scenes before MAX DETAIL acknowledgement. OFF clears the earlier request without overwriting the new scene.");
            if (_owned.HistoryChanged)
                return new(false, true, "The game changed scene history; MAX DETAIL acknowledgement and cleanup are not verified.");
            if (state.Pending == 0)
            {
                if (_owned.RequestedMode == _owned.OriginalMode)
                {
                    _owned = null;
                    _requestedIdentity = null;
                    _returnOrigin = null;
                    return new(true, false, "Original MAX DETAIL render mode verified restored.");
                }
                return new(true, true, "MAX DETAIL enabled; original render mode captured for restoration.");
            }
            Thread.Sleep(10);
        } while (clock.Elapsed < _timeout);
        return new(false, true, "MAX DETAIL transition is still pending; wait, then use OFF or RESET STATE. Cleanup is not verified.");
    }

    private void Observe(IMaxDetailRenderSession session, RenderModeState state, MaxDetailSceneState? scene)
    {
        var identity = session.Identity;
        if (_returnOrigin is { } origin)
        {
            if (origin.Identity != identity) _returnOrigin = null;
            else if (scene is { Kind: MaxDetailSceneKind.Photo } photo)
            {
                if ((photo.SavedReturnMode == session.Modes.Detail && (origin.Photo is null || origin.OwnsCache) ||
                     photo.SavedReturnMode == origin.Mode && !origin.OwnsCache) &&
                    (origin.Photo is null || origin.Photo.Value.SameScene(photo)))
                {
                    _returnOrigin = origin with { Photo = photo, OwnsCache = photo.SavedReturnMode == session.Modes.Detail };
                    // Renderer history can change during native entry while
                    // the UI is still Photo Mode. Its correct active baseline
                    // is Photo, not the normal view held in its return cache.
                    if (state.Current == session.Modes.Detail && state.Option == 0 && state.Pending == 0 && photo.Phase == 0)
                        _owned = new(identity, session.Modes.Photo, 0, session.Modes.Detail,
                            session.Modes.Detail, state.Previous, Scene: photo);
                }
                else _returnOrigin = null; // The game changed/replaced the cache; don't overwrite it.
            }
            else if (origin.Photo is { } priorPhoto && scene is { } nextScene &&
                     (priorPhoto.Root != nextScene.Root || priorPhoto.Table != nextScene.Table))
                _returnOrigin = null;
            else if (origin.Photo is not null && scene is { Kind: MaxDetailSceneKind.Empty or MaxDetailSceneKind.Garage } empty &&
                     state.Current == session.Modes.Detail && state.Option == 0 && state.Pending == 0)
            {
                // The observed Photo scene has gone and replayed our cached
                // override. Restore the captured normal origin on explicit OFF.
                _owned = new(identity, origin.Mode, origin.Option, session.Modes.Detail,
                    session.Modes.Detail, state.Previous, Scene: empty);
                _requestedIdentity = identity;
                _returnOrigin = null;
            }
            else if (state.Pending == 0 && state.Current != session.Modes.Detail &&
                     scene is { Kind: MaxDetailSceneKind.Empty or MaxDetailSceneKind.Garage } &&
                     (origin.Photo is null || state.Current != origin.Mode || state.Option != origin.Option))
                _returnOrigin = null;
        }
        Reconcile(identity, state, scene);
    }

    private void Reconcile(RenderModeIdentity identity, RenderModeState state, MaxDetailSceneState? scene)
    {
        if (_requestedIdentity is { } requested && requested != identity)
        {
            _owned = null;
            _requestedIdentity = null;
            _returnOrigin = null;
            return;
        }
        if (_owned is not { } owned) return;
        if (owned.Scene is { } captured && (scene is not { } active || !captured.SameScene(active)))
        {
            _owned = state.Current == owned.DetailMode ? owned with { HistoryChanged = true } : null;
            return;
        }
        if (owned.RequestedMode == owned.OriginalMode && state.Current == owned.OriginalMode &&
            state.Option == owned.OriginalOption && state.Pending == 0)
        {
            _owned = null;
            _requestedIdentity = null;
            _returnOrigin = null;
        }
        else if (!owned.PublicationFailed && owned.Matches(state))
            _owned = owned with { HistoryChanged = false };
        else if (!owned.PublicationFailed && owned.Scene is { Kind: MaxDetailSceneKind.Photo } &&
                 scene is { Kind: MaxDetailSceneKind.Photo, Phase: 0 } &&
                 owned.OriginalMode == MaxDetailModeMap.Steam461.Photo &&
                 state.Current == owned.DetailMode && state.Option == 0 && state.Pending == 0)
            _owned = owned with { ExpectedPrevious = state.Previous, HistoryChanged = false };
        else if (state.Current == owned.DetailMode)
            // Do not discard an unresolved full-detail override and report OFF.
            // Nor use its stale original view to overwrite a native garage.
            _owned = owned with { HistoryChanged = !owned.PublicationFailed };
        else if (!owned.PublicationFailed || state.Current != owned.OriginalMode || state.Pending == 0)
            _owned = null;
    }

    private MaxDetailResult SceneChanged()
    {
        _requestedIdentity = null;
        _returnOrigin = null;
        return new(true, false, "Game changed the render scene or process; MAX DETAIL request cleared without overwriting it.");
    }
}
