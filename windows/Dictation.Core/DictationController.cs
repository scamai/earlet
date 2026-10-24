using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>
/// The Windows entry point's behaviour: enabling, pausing, polling the hold
/// key, keeping the speech worker alive, and reporting what happened — with
/// every Win32 edge behind <see cref="IDictationPlatform"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One thread.</b> Every public member is called on the UI thread, and
/// session events — raised on whatever thread the pipeline happens to be on —
/// are marshalled back to it through the <c>post</c> delegate (the WPF
/// dispatcher in the app, a queue in tests) before they touch any state. So
/// are the ends of background work: a worker restart and the press-time
/// field check report back through <c>post</c>.
/// </para>
/// <para>
/// <b>Only the current session speaks.</b> A posted session event runs
/// later than it was raised. By then the user may have paused, which cancels
/// that session (raising Armed and Cancelled on the way out) and tears the
/// hook down. Each handler therefore checks that its sender is still the
/// session this controller holds, and a detached session's events are
/// dropped instead of repainting the window with "Enabled" and a live Pause
/// button over a microphone that is already off.
/// </para>
/// </remarks>
public sealed partial class DictationController : IDisposable
{
    private readonly IDictationPlatform _platform;
    private readonly IDictationView _view;
    private readonly Action<Action> _post;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly Func<string, string?> _environment;
    private readonly string _appDirectory;
    private readonly DictationControllerOptions _options;
    private readonly DictationSettingsStore _settingsStore;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CorrectionWatch? _watch;
    private readonly DictationLearning? _learning;
    private readonly DictationHudFeed? _hud;
    private readonly Action<long, int>? _keyboardFeed;

    private DictationSettings _settings;
    private HotkeyBinding _holdKey;
    private readonly bool _holdKeyFromEnvironment;
    private bool _launchAtLogin;

    // ─── Per arm ────────────────────────────────────────────────────────
    private IDictationTrigger? _trigger;
    private HoldGate? _gate;
    private PushToTalkController? _hotkey;
    private IAudioCaptureDevice? _microphone;
    private PushToTalkMicrophoneTap? _tap;
    private DictationInsertionEdges? _edges;
    private DictationSession? _session;
    private DictationState _shownState = DictationState.Idle;
    private bool _wasDown;

    // ─── Per hold ───────────────────────────────────────────────────────
    private Task<SecureFieldState>? _fieldCheck;
    private int _press;
    private TimeSpan? _holdStartedAt;
    private TriggerCancel _cancelling;

    /// <summary>The key is down on a press refused because the last
    /// dictation was still going in; nothing records until it comes up.</summary>
    private bool _pressRefused;

    // ─── The worker ─────────────────────────────────────────────────────
    private DictationWorkerSlot? _worker;
    private TimeSpan _lastWorkerUse;
    private bool _restarting;
    private DictationStatus? _restartAnnouncement;
    private int _restartAttempts;
    private bool _workerResetExpected;

    // ─── The window ─────────────────────────────────────────────────────
    private bool _enabling;
    private bool _disposed;
    private bool _closePending;
    private bool _closeForced;
    private bool _revealOnFailure;
    private DictationStatus _status = new(DictationCopy.Ready);

    public DictationController(
        IDictationPlatform platform,
        IDictationView view,
        Action<Action> post,
        ILoggerFactory logs,
        DictationSettingsStore settings,
        Func<string, string?> environment,
        string appDirectory,
        DictationControllerOptions? options = null,
        DictationLearning? learning = null,
        IDictationHud? hud = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _log = logs.CreateLogger<DictationController>();
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _appDirectory = appDirectory ?? throw new ArgumentNullException(nameof(appDirectory));
        _options = options ?? new DictationControllerOptions();
        _options.HoldPolicy.Checked();
        _settingsStore = settings ?? throw new ArgumentNullException(nameof(settings));
        _learning = learning;
        if (hud is not null)
        {
            _hud = new DictationHudFeed(hud, _options.Clock, () => HudHold, logs.CreateLogger<DictationHudFeed>());
        }

        if (learning is not null)
        {
            _watch = new CorrectionWatch(
                learning,
                _platform.CreateSpanObserverTarget,
                logs.CreateLogger<CorrectionWatch>(),
                _options.Observer,
                _options.Time,
                _options.ObserverDelay);
            _keyboardFeed = _watch.Keyboard.Record;
        }

        // Resolved once: the override cannot change while the app runs, and
        // the saved choice changes only through ChooseHoldKey.
        _settings = _settingsStore.Load();
        _holdKeyFromEnvironment = !string.IsNullOrWhiteSpace(_environment(DictationEnvironment.HoldKey));
        _holdKey = DictationHotkeys.ResolveHold(
            warning => _log.LogWarning("{Warning}", warning),
            _settings.HoldVirtualKey,
            _environment(DictationEnvironment.HoldKey));
    }

    /// <summary>The keys the picker offers, in order.</summary>
    public IReadOnlyList<HotkeyBinding> HoldKeyChoices => DictationHotkeys.HoldChoices;

    /// <summary>The hold key in effect.</summary>
    public HotkeyBinding HoldKey => _holdKey;

    /// <summary>The languages the picker offers, in order.</summary>
    public IReadOnlyList<SpeechLanguage> LanguageChoices => SpeechLanguages.Choices;

    /// <summary>The last state rendered.</summary>
    public DictationViewState? Shown { get; private set; }

    /// <summary>Whether a session is armed on the hold key right now.</summary>
    public bool IsEnabled => _session is not null;

    /// <summary>The correction watch, or null when no learning was composed.</summary>
    public CorrectionWatch? Corrections => _watch;

    // ─── Launch, settings and the window's buttons ──────────────────────

    /// <summary>
    /// Paint the first state, and turn dictation back on when the user left it
    /// on — without asking anything, and only when the speech resources are
    /// where the package puts them.
    /// </summary>
    /// <param name="atLogin">Started by the sign-in entry: when dictation is
    /// coming back on, the window stays hidden and the notification-area icon
    /// is the whole presence.</param>
    public DictationStartup Start(bool atLogin = false)
    {
        ReadLaunchAtLogin();
        // What the last run left: a take a crash or a quit left, offered now.
        OfferSavedTakeAtLaunch();
        _log.LogInformation(
            "Hold key {Key}{Source}; language {Language}; "
            + "enabled last time: {Enabled}; started at sign-in: {AtLogin}; learning from corrections: {Learning}",
            _holdKey.Display,
            _holdKeyFromEnvironment ? $" (from {DictationEnvironment.HoldKey})" : string.Empty,
            _settings.Language ?? "automatic",
            _settings.Enabled,
            atLogin,
            _settings.LearnFromCorrections);
        Render();

        if (!_settings.Enabled)
        {
            return new DictationStartup(ShowWindow: true, Task.CompletedTask);
        }

        // The same enable the button runs, so missing resources or a model
        // that will not load say so the same way — and leave the choice on,
        // so repairing the install brings it back at the next launch.
        _log.LogInformation("Dictation was on when it last ran; turning it back on");
        _revealOnFailure = atLogin;
        // A take a closing Dictation left brings the window forward even at
        // sign-in, where its Saved recording section offers it.
        return new DictationStartup(ShowWindow: !atLogin || OfferedInterruptedTake, EnableAsync());
    }

    /// <summary>
    /// Use <paramref name="virtualKey"/> as the hold key from now on, and
    /// remember it. Re-arms the hook at once when dictation is on.
    /// </summary>
    /// <remarks>
    /// Ignored while the override sets the key, while enabling, and while a
    /// dictation is running — the picker is disabled then, and a change
    /// under a held key would tear down the hook mid-sentence.
    /// </remarks>
    public void ChooseHoldKey(uint virtualKey)
    {
        var choice = DictationHotkeys.HoldChoices.FirstOrDefault(c => c.VirtualKey == virtualKey);
        if (!CanChooseHoldKey || choice is null || choice.VirtualKey == _holdKey.VirtualKey)
        {
            return;
        }

        _settings = _settings with { HoldVirtualKey = choice.VirtualKey };
        var saved = _settingsStore.Save(_settings);
        _holdKey = choice;
        _log.LogInformation("Hold key changed to {Key} (saved: {Saved})", choice.Display, saved);

        DictationStatus? status = saved ? null : new DictationStatus(DictationCopy.HoldKeyNotSaved);
        if (_session is not null)
        {
            StopTrigger();
            if (!Arm())
            {
                Fail(DictationCopy.ShortcutRefused);
                return;
            }

            // Under a blocked microphone the new key cannot be used either:
            // the blocked sentence stays, with its buttons.
            status ??= ReadyUnlessBlocked(DictationCopy.Armed(_holdKey));
        }

        Render(status);
    }

    private bool AtRest =>
        !_disposed && !_enabling && !_closePending
        && (_session is null || _session.State is DictationState.Idle or DictationState.Armed);

    private bool CanChooseHoldKey => AtRest && !_holdKeyFromEnvironment;

    private bool CanChooseLanguage => AtRest && !_restarting;

    /// <summary>
    /// Recognise <paramref name="code"/> from now on (null: detect it), and
    /// remember it. The engine takes the language per request, so the worker
    /// already loaded simply decodes the next hold with it — no restart, no
    /// model reload. The language is read when a hold's decode request is
    /// built, so what keeps a dictation under way on the language it began
    /// with is <see cref="CanChooseLanguage"/> refusing the change until the
    /// controller is at rest — unlike the Mac, whose picker stays live and
    /// fixes the language when a hold starts.
    /// </summary>
    public void ChooseLanguage(string? code)
    {
        if (!SpeechLanguages.IsOffered(code))
        {
            return;
        }

        var choice = SpeechLanguages.Find(code);
        if (!CanChooseLanguage || choice.Code == _settings.Language)
        {
            return;
        }

        _settings = _settings with { Language = choice.Code };
        var saved = _settingsStore.Save(_settings);
        _log.LogInformation("Language changed to {Language} (saved: {Saved})", choice.Code ?? "automatic", saved);

        if (_worker is not null)
        {
            _worker.Language = choice.Code;
        }

        Render(saved ? DictationCopy.LanguageChanged(choice) : DictationCopy.LanguageNotSaved);
    }

    /// <summary>
    /// "Learn from my corrections": watch delivered dictations for the user's
    /// fixes, or stop. Off ends a watch that is running, without learning from
    /// it, and unlearns nothing. Remembered.
    /// </summary>
    public void SetLearnFromCorrections(bool on)
    {
        if (_disposed || on == _settings.LearnFromCorrections)
        {
            return;
        }

        _settings = _settings with { LearnFromCorrections = on };
        var saved = _settingsStore.Save(_settings);
        _log.LogInformation("Learning from corrections turned {State} (saved: {Saved})", on ? "on" : "off", saved);
        if (!on)
        {
            // The hook's feed comes off at the next tick, once the watch is over.
            _watch?.End(CorrectionWatchEnd.SwitchedOff);
        }

        Render(saved ? null : new DictationStatus(DictationCopy.LearnFromCorrectionsNotSaved));
    }

    /// <summary>Show the diagnostic log's folder, which every failure
    /// sentence points at.</summary>
    /// <returns>False when it could not be shown, and the status line names
    /// it instead; the menu then brings the window forward, so the sentence
    /// is not left behind a hidden window.</returns>
    public bool OpenLogFolder()
    {
        var opened = false;
        try
        {
            opened = _platform.OpenFolder(_options.LogDirectory);
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Opening the log folder failed");
        }

        if (!opened)
        {
            Render(DictationCopy.LogFolderNotOpened);
        }

        return opened;
    }

    /// <summary>
    /// Ask Windows again whether Dictation may use the microphone, while this
    /// run last learned that it may not. The window calls this when it is
    /// activated, which is how the user comes back from the privacy page, and
    /// from its Check again button. Access no longer blocked clears the blocked
    /// sentence and brings back "Try dictation"; any other status line (a
    /// language change made since, say) is left alone.
    /// </summary>
    /// <param name="checkAgain">The button: when nothing changed, say so, so a
    /// click is never met with silence.</param>
    public void RecheckMicrophone(bool checkAgain = false)
    {
        // Every blocked sentence sets the flag, so this also covers the line.
        if (_disposed || !_microphoneBlocked)
        {
            return;
        }

        MicrophoneAccess access;
        try
        {
            access = _platform.MicrophoneAccess.Probe();
        }
        catch (Exception error)
        {
            // Called from window activation: a probe that cannot answer must
            // not take the window down. The sentence on screen stays.
            _log.LogWarning(error, "Microphone access could not be read again");
            return;
        }

        if (DictationCopy.ForMicrophone(access) is null)
        {
            MicrophoneNoLongerBlocked(access);
        }
        else if (checkAgain)
        {
            var still = DictationCopy.ForMicrophoneStillBlocked(access)!;
            // Off, it still says paused, so the same answer is the same
            // sentence, which the window shows again visibly.
            Render(_session is null
                ? DictationCopy.PausedWhileBlocked(still)
                : DictationCopy.WithHoldKeySentence(still, DictationCopy.HoldKeySentenceIn(_status, _holdKey)));
        }
    }

    // ─── Enable and pause ───────────────────────────────────────────────

    /// <summary>Start the worker, load the model and arm the hold key.</summary>
    public async Task EnableAsync()
    {
        if (_disposed || _enabling || _session is not null)
        {
            return;
        }

        _enabling = true;
        Render(DictationCopy.Preparing);
        try
        {
            var plan = DictationWorkerPlan.Resolve(_appDirectory, _environment) with
            {
                Language = _settings.Language,
                TranscribeTimeout = _options.Takes.DecodeDeadline,
            };
            _log.LogInformation(
                "Enabling dictation: python={Python} runtime={Runtime} model={Model} language={Language}",
                plan.PythonExecutable, plan.RuntimeDirectory, plan.ModelDirectory, plan.Language ?? "automatic");
            var missing = plan.MissingFiles();
            if (missing.Count > 0)
            {
                _log.LogWarning("Speech resources are missing: {Missing}", string.Join("; ", missing));
                Fail(DictationCopy.ResourcesMissing);
                return;
            }

            var worker = new DictationWorkerSlot(_platform, _logs, plan);
            worker.DecodeRetried += OnDecodeRetried;
            _worker = worker;
            var ready = await worker.StartAsync();

            if (_disposed || !ReferenceEquals(worker, _worker))
            {
                return;
            }

            if (!ready)
            {
                Fail(DictationCopy.ModelDidNotLoad);
                return;
            }

            if (!Arm())
            {
                Fail(DictationCopy.ShortcutRefused);
                return;
            }

            _enabling = false;
            _restartAttempts = 0;
            _lastWorkerUse = _options.Clock();
            Remember(enabled: true);
            _log.LogInformation("Dictation enabled on {Key}", _holdKey.Display);

            // Asked now rather than at the first hold, so a switched-off
            // microphone is named while the user is looking at this window
            // instead of surfacing as a failed sentence later. This answer
            // replaces whatever an earlier run of dictation learned.
            _microphoneBlocked = false;
            var blocked = DictationCopy.ForMicrophone(_platform.MicrophoneAccess.Probe());
            Render(blocked ?? new DictationStatus(DictationCopy.Armed(_holdKey)));
            if (_revealOnFailure && blocked is not null)
            {
                // On, but hidden — enabled from the notification area, started
                // at sign-in, or closed while enabling — with Windows blocking
                // the microphone: the warning icon alone would hide the fix,
                // so the window comes forward with it, as after a failed
                // Enable. The Mac's menu offers "Finish permission setup…",
                // which opens its window, and its sign-in launch shows the
                // window while a grant is missing.
                _view.Reveal();
            }

            _revealOnFailure = false;
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                _log.LogError(error, "Enabling dictation failed");
            }

            Fail(DictationCopy.EnableFailed);
        }
        finally
        {
            _enabling = false;
        }
    }

    /// <summary>Turn the microphone, hook and worker off, and remember that
    /// the user turned them off. Discards a dictation under way, so the app
    /// never calls it directly: the window and the menu go through
    /// <see cref="TryPause"/>, which refuses then. Internal for the tests,
    /// which call it under a dictation to pin the teardown of a live hold
    /// (the same <see cref="Stop"/> that quitting and a failure run) and the
    /// detached session's late events.</summary>
    internal void Pause()
    {
        _log.LogInformation("Dictation paused");
        _watch?.End(CorrectionWatchEnd.Paused);
        Stop();
        Remember(enabled: false);
        // While this run knows Windows blocks the microphone, the window says
        // so and keeps the fix on offer, as the icon keeps its warning
        // (DictationTrayGlyph): the Mac's header shows its setup step while
        // dictation is off and a grant is missing.
        Render(_microphoneBlocked && _microphoneBlockedBy is { } blocked
            ? DictationCopy.PausedWhileBlocked(blocked)
            : new DictationStatus(DictationCopy.Paused));
    }

    /// <summary>
    /// The window's Pause button and the menu's Pause dictation: pause, unless
    /// a dictation is under way, which Cancel ends instead. A button or a menu
    /// drawn before the press is not a choice to discard it, so a click that
    /// lands then changes nothing, as the Mac's toggle() refuses while any
    /// attempt exists. Both are inactive while one listens, transcribes or
    /// types (<see cref="DictationViewState.CanPause"/>, one rule), but they
    /// are drawn from the last repaint; this asks the press itself: a field
    /// check still out (the Mac's privacy gate, during which its button is
    /// live too), or a hold the session already admitted whose repaint has
    /// not reached the window yet.
    /// </summary>
    /// <returns>Whether dictation was paused.</returns>
    public bool TryPause()
    {
        if (!PauseAllowed
            || _fieldCheck is not null
            || _session?.State is not (DictationState.Idle or DictationState.Armed))
        {
            return false;
        }

        Pause();
        return true;
    }

    /// <summary>The notification-area menu's Enable/Pause item.</summary>
    public Task ToggleAsync()
    {
        if (_session is null)
        {
            // The notification area is how a hidden window is reached, and
            // the icon alone would hide why an enable failed: a failure brings
            // the window forward. One already showing is only activated.
            _revealOnFailure = true;
            return EnableAsync();
        }

        TryPause();
        return Task.CompletedTask;
    }

    private void Remember(bool enabled)
    {
        if (_settings.Enabled == enabled)
        {
            return;
        }

        _settings = _settings with { Enabled = enabled };
        if (!_settingsStore.Save(_settings))
        {
            _log.LogWarning("Whether dictation is on could not be saved; the next launch may differ");
        }
    }
}
