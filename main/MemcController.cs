using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Memc;

namespace VRCNext;

// Memory Console wiring.
//
// This file owns the composition: it knows which VRCNext component owns which
// resource. The engine under Services/Memc has no domain knowledge at all.
//
// Nothing in here runs until /memc true is issued. SetupMemc only assigns three
// delegates on the manager; the registrar itself is invoked by MemoryManager the
// first time the console is enabled.
public partial class AppShell
{
    private readonly MemoryManager _memc = new();

    private void SetupMemc()
    {
        _memc.Send = (type, payload) => Invoke(() => SendToJS(type, payload));
        _memc.Log = (msg, color) => Invoke(() => SendToJS("log", new { msg, color }));
        _memc.Registrar = MemcRegisterAll;
        _memc.ActiveToolsProvider = MemcToolStates;
    }

    private Dictionary<string, bool> MemcToolStates() => new()
    {
        ["kikitanXd"]       = _kxdCtrl?.IsRunning ?? false,
        ["voiceFight"]      = _vfCtrl?.IsRunning ?? false,
        ["spaceFlight"]     = _sfCtrl?.IsConnected ?? false,
        ["frameShot"]       = _fsCtrl?.IsConnected ?? false,
        ["discordPresence"] = _discordCtrl?.IsConnected ?? false,
        ["mediaRelay"]      = _relayCtrl?.IsRunning ?? false,
        ["chatbox"]         = _chatboxCtrl?.IsEnabled ?? false,
        ["eventSnipe"]      = _snipeCtrl?.IsRunning ?? false,
        ["vrOverlay"]       = MemcVroConnected(),
        ["vrcRunning"]      = RelayController.IsVrcRunning(),
        ["steamVrRunning"]  = RelayController.IsSteamVrRunning(),
        ["multiTaskMode"]   = _settings.MultiTaskMode,
        ["tilingManager"]   = _settings.TilingManager,
        ["memoryTrim"]      = _settings.MemoryTrimEnabled,
    };

    private bool MemcVroConnected()
    {
#if WINDOWS
        return _core?.VrOverlay?.VroConnected ?? false;
#else
        return false;
#endif
    }

    // Module graph

    private void MemcRegisterAll(MemoryModuleTracker t)
    {
        MemcRegisterBase(t);
        MemcRegisterBridge(t);
        MemcRegisterAvatarDb(t);
        MemcRegisterServices(t);
        MemcRegisterTools(t);
        MemcRegisterSelf(t);
    }

    // WebView bridge
    //
    // Counts what actually crosses into the WebView, per message type. These are
    // cumulative flow counters, not resident memory, so they carry MemQuality.Throughput
    // and never enter the attribution sums. Their series slope is bytes per minute,
    // which is what makes a chatty message type visible as a leak driver.
    private void MemcRegisterBridge(MemoryModuleTracker t)
    {
        MemcResetBridgeStats();

        var m = t.Module("bridge", "WebView Bridge");
        m.IsActive = () => true;

        m.Add(new MemResource
        {
            Id = "sentBytes", Label = "Bytes handed to SendWebMessage (all types)",
            Category = MemCategory.Bridge,
            Quality = MemQuality.Throughput,
            Note = "Cumulative since the Memory Console was enabled. The native WebView layer "
                 + "copies every one of these strings; those copies are what shows up as "
                 + "unattributed native growth.",
            Bytes = () => Interlocked.Read(ref _bridgeSentBytes),
            Count = () => Interlocked.Read(ref _bridgeSentCount),
        });
        m.Add(new MemResource
        {
            Id = "dispatchedBytes", Label = "Bytes actually dispatched to the WebView",
            Category = MemCategory.Bridge,
            Quality = MemQuality.Throughput,
            Note = "Counted in the dispatcher loop, after friends-coalescing. A gap to the line "
                 + "above means messages were merged or are still queued.",
            Bytes = () => Interlocked.Read(ref _bridgeDispatchedBytes),
            Count = () => Interlocked.Read(ref _bridgeDispatchedCount),
        });
        m.Add(new MemResource
        {
            Id = "queueBacklog", Label = "Enqueued but not yet dispatched",
            Category = MemCategory.Bridge,
            Quality = MemQuality.CountOnly,
            Note = "A number that keeps climbing means the WebView cannot keep up with the "
                 + "producer and messages are piling up in the channel.",
            Count = () => Math.Max(0, Interlocked.Read(ref _bridgeSentCount)
                                    - Interlocked.Read(ref _bridgeDispatchedCount)),
        });

        // One row per message type, discovered at runtime. Add is idempotent by id,
        // so re-adding an existing type just replaces the delegate.
        m.OnBeforeSnapshot = () =>
        {
            foreach (var kv in _bridgeStats)
            {
                var type = kv.Key;
                var stat = kv.Value;
                m.Add(new MemResource
                {
                    Id = "type." + type,
                    Label = type,
                    Category = MemCategory.Bridge,
                    Quality = MemQuality.Throughput,
                    Bytes = () => Interlocked.Read(ref stat.Bytes),
                    Count = () => Interlocked.Read(ref stat.Count),
                });
            }
        };
    }

    private void MemcRegisterBase(MemoryModuleTracker t)
    {
        var m = t.Module("base", "VRCNext Base");
        m.IsActive = () => true;

        m.Add(new MemResource
        {
            Id = "jsQueue", Label = "Pending JS messages",
            Category = MemCategory.Bridge,
            Quality = MemQuality.CountOnly,
            Note = "Unbounded channel feeding SendWebMessage. A rising count means the WebView is not draining fast enough.",
            Count = () => _jsQueue.Reader.CanCount ? _jsQueue.Reader.Count : -1,
        });
        m.Add(new MemResource
        {
            Id = "lastFriendsPayload", Label = "Last friends payload size",
            Category = MemCategory.Bridge,
            Quality = MemQuality.Instrumented,
            Note = "Byte size of the most recent vrcFriends JSON handed to the WebView.",
            Bytes = () => Interlocked.Read(ref _lastFriendsPayloadBytes),
        });
        m.Add(new MemResource
        {
            Id = "sharedContent", Label = "Shared content cache",
            Category = MemCategory.Managed,
            Quality = MemQuality.Instrumented,
            Count = () => { lock (_sharedContentCacheLock) return _sharedContentCache?.Count ?? 0; },
            Bytes = () =>
            {
                lock (_sharedContentCacheLock)
                {
                    if (_sharedContentCache == null) return 0;
                    return MemorySizer.OfStringPairMap(_sharedContentCache);
                }
            },
        });
        m.Add(new MemResource
        {
            Id = "ownAvatars", Label = "Own avatar cache",
            Category = MemCategory.Managed,
            Quality = MemQuality.Instrumented,
            Count = () => _ownAvatarCache?.Count ?? 0,
            Bytes = () => _ownAvatarCache == null ? 0 : MemorySizer.OfStringPairMap(_ownAvatarCache),
        });
        m.Add(new MemResource
        {
            Id = "vrcCacheScanned", Label = "Scanned VRChat cache files",
            Category = MemCategory.Managed,
            Quality = MemQuality.Instrumented,
            Note = "Grows for the whole session; entries are never removed.",
            Count = () => _vrcCacheScanned.Count,
            Bytes = () => MemorySizer.OfStringSet(_vrcCacheScanned),
        });
        m.Add(new MemResource
        {
            Id = "amplitudeSnapshot", Label = "Amplitude cache snapshot",
            Category = MemCategory.Managed,
            Quality = MemQuality.Instrumented,
            Note = "Held between 100 ms polls for change detection.",
            Bytes = () => MemorySizer.OfString(_amplitudeLast),
        });
        m.Add(new MemResource
        {
            Id = "vrWorldCacheShell", Label = "VR world cache (shell copy)",
            Category = MemCategory.Managed,
            Quality = MemQuality.Instrumented,
            Count = () => { lock (_vrWorldCache) return _vrWorldCache.Count; },
            Bytes = () => { lock (_vrWorldCache) return MemorySizer.OfStringPairMap(_vrWorldCache); },
        });
        m.Add(new MemResource
        {
            Id = "webview2", Label = "WebView2 renderer + GPU processes",
            Category = MemCategory.Native,
            Quality = MemQuality.NotMeasurable,
            Note = "Chromium runs in separate msedgewebview2.exe processes. Their memory is not part of "
                 + "VRCNext.exe and cannot be read through the .NET APIs available here.",
        });
    }

    private void MemcRegisterAvatarDb(MemoryModuleTracker t)
    {
        var m = t.Module("avatardb", "Avatar DB Submission");
        m.IsActive = () => _settings.AvtrdbSubmitAvatars || _settings.VrcndbSubmitAvatars || _settings.AvtrIcuSubmitAvatars;

        void Set(string id, string label, Func<int> count, Func<long> bytes, string? note = null) =>
            m.Add(new MemResource
            {
                Id = id, Label = label, Category = MemCategory.Managed,
                Quality = MemQuality.Instrumented, Note = note,
                Count = () => count(), Bytes = bytes,
            });

        Set("checkedIds", "Checked avatar ids", () => _checkedAvatarIds.Count,
            () => MemorySizer.OfStringSet(_checkedAvatarIds),
            "Session-scoped, never trimmed.");
        Set("deletedIds", "Known deleted avatar ids", () => _deletedAvatarIds.Count,
            () => MemorySizer.OfStringSet(_deletedAvatarIds),
            "Loaded from the avtrdb deletion cache at startup.");
        Set("avtrdbSets", "avtrdb reported/submitted sets", () => _reportedToAvtrdb.Count + _avtrdbSubmittedIds.Count,
            () => MemorySizer.OfStringSet(_reportedToAvtrdb) + MemorySizer.OfStringSet(_avtrdbSubmittedIds));
        Set("icuSets", "ICU reported/submitted sets", () => _reportedToAvtrIcu.Count + _avtrIcuSubmittedIds.Count,
            () => MemorySizer.OfStringSet(_reportedToAvtrIcu) + MemorySizer.OfStringSet(_avtrIcuSubmittedIds));
        Set("vrcndbSets", "VRCNDb submitted/rechecked sets", () => _vrcndbSubmittedIds.Count + _vrcndbRecheckedIds.Count,
            () => MemorySizer.OfStringSet(_vrcndbSubmittedIds) + MemorySizer.OfStringSet(_vrcndbRecheckedIds));
        Set("pendingQueues", "Pending submit/report queues",
            () => _avtrdbReportQueue.Count + _avtrdbSubmitQueue.Count + _avtrIcuReportQueue.Count
                + _avtrIcuSubmitQueue.Count + _vrcndbSubmitQueue.Count + _vrcndbRecheckQueue.Count,
            () => MemorySizer.ListOverhead(_avtrdbReportQueue.Count) + MemorySizer.SumStrings(_avtrdbReportQueue)
                + MemorySizer.ListOverhead(_avtrdbSubmitQueue.Count) + MemorySizer.SumStrings(_avtrdbSubmitQueue)
                + MemorySizer.ListOverhead(_avtrIcuReportQueue.Count) + MemorySizer.SumStrings(_avtrIcuReportQueue)
                + MemorySizer.ListOverhead(_avtrIcuSubmitQueue.Count) + MemorySizer.SumStrings(_avtrIcuSubmitQueue)
                + MemorySizer.ListOverhead(_vrcndbSubmitQueue.Count) + MemorySizer.SumStrings(_vrcndbSubmitQueue)
                + MemorySizer.ListOverhead(_vrcndbRecheckQueue.Count) + MemorySizer.SumStrings(_vrcndbRecheckQueue),
            "Flushed by timers; a queue that never drains is a stuck flush.");
    }

    private void MemcRegisterServices(MemoryModuleTracker t)
    {
        var db = t.Module("database", "Database");
        db.IsActive = () => true;
        _core.TimeEngine?.MemcRegister(db);
        _core.PhotoPlayersStore?.MemcRegister(db);
        db.Add(new MemResource
        {
            Id = "sqlitePageCaches", Label = "SQLite page caches",
            Category = MemCategory.Database,
            Quality = MemQuality.NotMeasurable,
            Note = "Every connection is opened with PRAGMA cache_size=-1024 (1 MB cap), but "
                 + "Microsoft.Data.Sqlite exposes no live per-connection usage counter.",
        });

        var tl = t.Module("timeline", "Timeline");
        tl.IsActive = () => true;
        _core.Timeline?.MemcRegister(tl);

        var img = t.Module("imagecache", "Image Cache");
        img.IsActive = () => true;
        VRCNext.Services.Helpers.ImageCacheHelper.MemcRegister(img);

        var fr = t.Module("friends", "Friends");
        fr.IsActive = () => _friends?.FriendStateSeeded ?? false;
        _friends?.MemcRegister(fr);

        var wo = t.Module("worlds", "Worlds");
        wo.IsActive = () => true;
        _core.World?.MemcRegister(wo);

        var inst = t.Module("instance", "Instance / Log Watcher");
        inst.IsActive = RelayController.IsVrcRunning;
        _logWatcher?.MemcRegister(inst);
        if (_instance != null)
        {
            inst.Add(new MemResource
            {
                Id = "cumulativePlayers", Label = "Cumulative instance players",
                Category = MemCategory.Managed,
                Quality = MemQuality.Instrumented,
                Note = "Reset on instance change.",
                Count = () => _instance.CumulativeInstancePlayers.Count,
                Bytes = () => MemorySizer.OfStringPairMap(_instance.CumulativeInstancePlayers),
            });
            inst.Add(new MemResource
            {
                Id = "joinLeaveTimes", Label = "Join / leave timestamp lists",
                Category = MemCategory.History,
                Quality = MemQuality.Instrumented,
                Count = () => _instance.PlayerJoinTimes.Count + _instance.PlayerLeftTimes.Count,
                Bytes = () => MemcSizeOfListMap(_instance.PlayerJoinTimes) + MemcSizeOfListMap(_instance.PlayerLeftTimes),
            });
            inst.Add(new MemResource
            {
                Id = "instanceSets", Label = "Meet-again / recently-closed sets",
                Category = MemCategory.Managed,
                Quality = MemQuality.Instrumented,
                Count = () => _instance.MeetAgainThisInstance.Count + _instance.RecentlyClosedLocs.Count,
                Bytes = () => MemorySizer.OfStringSet(_instance.MeetAgainThisInstance)
                            + MemorySizer.OfStringSet(_instance.RecentlyClosedLocs),
            });
        }

        var modal = t.Module("modalcache", "Modal Cache");
        modal.IsActive = () => true;
        VRCNext.Services.Helpers.ModalCacheHelper.MemcRegister(modal);

        var api = t.Module("vrcapi", "VRChat API");
        api.IsActive = () => _vrcApi?.IsLoggedIn ?? false;
        api.Add(new MemResource
        {
            Id = "httpClient", Label = "HttpClient connection pool",
            Category = MemCategory.Native,
            Quality = MemQuality.NotMeasurable,
            Note = "SocketsHttpHandler exposes no allocation counter. Buffers are pooled and short-lived.",
        });
        api.Add(new MemResource
        {
            Id = "responseBuffers", Label = "Retained API response buffers",
            Category = MemCategory.Buffers,
            Quality = MemQuality.NotMeasurable,
            Note = "Responses are parsed into JObject and released. What survives is attributed to the "
                 + "module that stores it (Friends, Worlds, Groups, ...).",
        });
    }

    private static long MemcSizeOfListMap(Dictionary<string, List<string>> d)
    {
        long b = MemorySizer.DictionaryOverhead(d.Count);
        foreach (var kv in d)
        {
            b += MemorySizer.OfString(kv.Key) + MemorySizer.ListOverhead(kv.Value.Count);
            foreach (var s in kv.Value) b += MemorySizer.OfString(s);
        }
        return b;
    }

    private void MemcRegisterTools(MemoryModuleTracker t)
    {
        var kx = t.Module("kikitan", "Kikitan XD");
        kx.IsActive = () => _kxdCtrl?.IsRunning ?? false;
        _kxdCtrl?.MemcRegister(kx);

        var vf = t.Module("voicefight", "Voice Fight");
        vf.IsActive = () => _vfCtrl?.IsRunning ?? false;
        _vfCtrl?.MemcRegister(vf);

#if WINDOWS
        // The VR host is created lazily when the first VR tool starts, which is usually
        // after /memc true. So the lambdas resolve _core.VrOverlay per sample instead of
        // capturing it once at registration time.
        var vro = t.Module("vrsubprocess", "VR Subprocess (Overlay / SpaceFlight / FrameShot)");
        vro.IsActive = () => _core?.VrOverlay?.AnyConnected ?? false;
        vro.Add(new MemResource
        {
            Id = "subprocessWorkingSet", Label = "VR subprocess working set",
            Category = MemCategory.Native,
            Quality = MemQuality.Measured,
            Note = "Separate process (VRCNext.exe --vr-subprocess). Real OS measurement, but deliberately "
                 + "not counted toward this process's attribution because it is not this process's memory.",
            Bytes = () => _core?.VrOverlay?.MemcSubprocessBytes(false) ?? 0,
        });
        vro.Add(new MemResource
        {
            Id = "subprocessPrivate", Label = "VR subprocess private memory",
            Category = MemCategory.Native,
            Quality = MemQuality.Measured,
            Note = "Separate process. Not part of this process's memory.",
            Bytes = () => _core?.VrOverlay?.MemcSubprocessBytes(true) ?? 0,
        });
        vro.Add(new MemResource
        {
            Id = "subprocessAlive", Label = "VR subprocess running",
            Category = MemCategory.Native,
            Quality = MemQuality.CountOnly,
            Count = () => (_core?.VrOverlay?.MemcSubprocessAlive ?? false) ? 1 : 0,
        });
#endif

        var sf = t.Module("spaceflight", "Space Flight");
        sf.IsActive = () => _sfCtrl?.IsConnected ?? false;
        sf.Add(new MemResource
        {
            Id = "hostSide", Label = "Host-side state",
            Category = MemCategory.Managed,
            Quality = MemQuality.CountOnly,
            Note = "The tracking loop runs in the VR subprocess. In VRCNext.exe this tool only forwards "
                 + "messages; its allocations are transient and appear under GC allocation rate.",
            Count = () => (_sfCtrl?.IsConnected ?? false) ? 1 : 0,
        });

        var fs = t.Module("frameshot", "FrameShot");
        fs.IsActive = () => _fsCtrl?.IsConnected ?? false;
        fs.Add(new MemResource
        {
            Id = "hostSide", Label = "Host-side state",
            Category = MemCategory.Managed,
            Quality = MemQuality.CountOnly,
            Note = "D3D11 textures and bitmaps live in the VR subprocess, measured under VR Subprocess.",
            Count = () => (_fsCtrl?.IsConnected ?? false) ? 1 : 0,
        });

        var cb = t.Module("chatbox", "Custom Chatbox");
        cb.IsActive = () => _chatboxCtrl?.IsEnabled ?? false;
        cb.Add(new MemResource
        {
            Id = "state", Label = "Chatbox enabled",
            Category = MemCategory.Managed, Quality = MemQuality.CountOnly,
            Count = () => (_chatboxCtrl?.IsEnabled ?? false) ? 1 : 0,
        });

        var dp = t.Module("discord", "Discord Presence");
        dp.IsActive = () => _discordCtrl?.IsConnected ?? false;
        dp.Add(new MemResource
        {
            Id = "state", Label = "RPC connected",
            Category = MemCategory.Managed, Quality = MemQuality.CountOnly,
            Count = () => (_discordCtrl?.IsConnected ?? false) ? 1 : 0,
        });

        var mr = t.Module("mediarelay", "Media Relay");
        mr.IsActive = () => _relayCtrl?.IsRunning ?? false;
        mr.Add(new MemResource
        {
            Id = "state", Label = "Relay running",
            Category = MemCategory.Managed, Quality = MemQuality.CountOnly,
            Count = () => (_relayCtrl?.IsRunning ?? false) ? 1 : 0,
        });

        var sn = t.Module("eventsnipe", "Event Snipe");
        sn.IsActive = () => _snipeCtrl?.IsRunning ?? false;
        sn.Add(new MemResource
        {
            Id = "state", Label = "Snipe running",
            Category = MemCategory.Managed, Quality = MemQuality.CountOnly,
            Count = () => (_snipeCtrl?.IsRunning ?? false) ? 1 : 0,
        });

        var mt = t.Module("multitask", "Multi Task Mode");
        mt.IsActive = () => _settings.MultiTaskMode;
        mt.Add(new MemResource
        {
            Id = "detachedWindows", Label = "Detached child windows",
            Category = MemCategory.Native,
            Quality = MemQuality.NotMeasurable,
            Note = "Detached windows are separate WebView2 surfaces. Their memory belongs to the "
                 + "msedgewebview2.exe processes, outside this profiler.",
        });
    }

    private void MemcRegisterSelf(MemoryModuleTracker t)
    {
        var m = t.Module("memc", "Memory Console (self)");
        m.IsActive = () => _memc.Enabled;
        m.Add(new MemResource
        {
            Id = "history", Label = "Sample ring buffers",
            Category = MemCategory.History,
            Quality = MemQuality.Instrumented,
            Note = "Fixed-capacity long[] rings. Capacity is set at construction and never grows.",
            Count = () => _memc.AllSeries.Count(),
            Bytes = () => _memc.HistoryBytes(),
        });
        m.Add(new MemResource
        {
            Id = "selfAlloc", Label = "Allocated per sample",
            Category = MemCategory.History,
            Quality = MemQuality.RuntimeCounter,
            Note = "GC.GetAllocatedBytesForCurrentThread delta across one sampling pass. This is the "
                 + "observer effect of the profiler itself.",
            Bytes = () => _memc.SelfAllocPerSample,
        });
    }

    // Console command: /memc

    internal VRCNext.Services.Helpers.ConsoleHelper.Result MemcCommand(string[] parts)
    {
        if (parts.Length < 2 || parts[1] == "?" || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
            return new(MemcHelpText + "\n\n" + _memc.StatusText(), "info");

        if (parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
            return new(_memc.StatusText(), "info");

        if (parts[1].Equals("interval", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3 || !int.TryParse(parts[2], out var ms))
                return new("Usage: /memc interval <250-60000>", "err");
            _memc.SetEnabled(_memc.Enabled, ms);
            return new($"Memory Console sampling interval set to {_memc.IntervalMs} ms.", "ok");
        }

        if (!bool.TryParse(parts[1], out var on))
            return new("Usage: /memc true | /memc false | /memc ? | /memc status | /memc interval <ms>", "err");

        var changed = _memc.SetEnabled(on);
        SendToJS("memcState", new { enabled = _memc.Enabled, intervalMs = _memc.IntervalMs });
        if (!changed)
            return new($"Memory Console already {(on ? "ON" : "OFF")}.", "warn");
        if (on)
            return new("Memory Console ENABLED. The Memory Manager button is now visible in the Activity Log toolbar.\n"
                     + _memc.StatusText(), "ok");
        return new("Memory Console DISABLED. Sampler thread stopped, history and registrations dropped.", "warn");
    }

    private const string MemcHelpText =
        "Memory Console (/memc)\n" +
        "\n" +
        "  /memc true            Enable sampling and show the Memory Manager button\n" +
        "  /memc false           Disable everything: thread, history, registrations\n" +
        "  /memc ?               This help plus current status\n" +
        "  /memc status          Current status only\n" +
        "  /memc interval <ms>   Sampling interval, 250 to 60000 ms (default 2000)\n" +
        "\n" +
        "While disabled there is no sampler thread, no timer, no history and no\n" +
        "module registration, so the profiler costs nothing.";

    // Frontend actions

    private async Task HandleMemcAction(string action, JObject msg)
    {
        switch (action)
        {
            case "memcState":
                SendToJS("memcState", new { enabled = _memc.Enabled, intervalMs = _memc.IntervalMs });
                break;

            case "memcOpen":
                _memc.ViewOpen = true;
                _memc.WantAllSeries = msg["allSeries"]?.Value<bool>() ?? false;
                // A freshly opened window has no cached static text, so send it all again.
                _memc.ResetPayloadCache();
                if (_memc.Enabled)
                {
                    SendToJS("memcLive", _memc.BuildLivePayload());
                    _memc.RequestImmediateSample();
                }
                break;

            case "memcDetail":
                _memc.WantAllSeries = msg["allSeries"]?.Value<bool>() ?? false;
                if (_memc.Enabled) _memc.RequestImmediateSample();
                break;

            case "memcClose":
                _memc.ViewOpen = false;
                break;

            case "memcRefresh":
                if (_memc.Enabled) SendToJS("memcLive", _memc.BuildLivePayload());
                break;

            case "memcDeep":
                if (_memc.Enabled)
                {
                    await Task.Run(() => _memc.DeepMeasure());
                    SendToJS("memcLive", _memc.BuildLivePayload());
                }
                break;

            case "memcCapture":
                if (_memc.Enabled)
                {
                    var slot = msg["slot"]?.ToString() ?? "A";
                    await Task.Run(() => _memc.Capture(slot));
                    SendToJS("memcLive", _memc.BuildLivePayload());
                }
                break;

            case "memcCompare":
                if (_memc.Enabled) SendToJS("memcCompare", _memc.BuildComparePayload());
                break;

            case "memcForceGc":
                if (_memc.Enabled)
                {
                    await Task.Run(() => _memc.ForceGc());
                    SendToJS("memcLive", _memc.BuildLivePayload());
                }
                break;

            case "memcExport":
                if (_memc.Enabled)
                {
                    try
                    {
                        var res = await Task.Run(() => MemoryAnalysisExporter.Export(_memc));
                        SendToJS("memcExported", new
                        {
                            ok = true, json = res.JsonPath, text = res.TextPath,
                            folder = res.Folder, bytes = res.JsonBytes,
                        });
                        SendToJS("log", new { msg = $"[MEMC] Analysis exported to {res.JsonPath}", color = "ok" });
                    }
                    catch (Exception ex)
                    {
                        SendToJS("memcExported", new { ok = false, error = ex.Message });
                        SendToJS("log", new { msg = $"[MEMC] Export failed: {ex.Message}", color = "err" });
                    }
                }
                break;

            case "memcOpenFolder":
                try
                {
                    Directory.CreateDirectory(MemoryAnalysisExporter.ExportDir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        MemoryAnalysisExporter.ExportDir) { UseShellExecute = true });
                }
                catch (Exception ex) { CrashHandler.WriteEntry("memcOpenFolder", ex); }
                break;
        }
    }
}
