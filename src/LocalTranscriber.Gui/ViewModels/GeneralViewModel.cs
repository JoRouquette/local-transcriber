using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Gui.Services;
using Microsoft.Win32;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page Général : dossiers, moteur, diarisation, token HF (avec validation inline).</summary>
public sealed partial class GeneralViewModel : ObservableValidator
{
    private readonly SettingsService _settings;
    private AppConfig C => _settings.Config;

    public GeneralViewModel(SettingsService settings)
    {
        _settings = settings;
        LoadFromConfig();
        _settings.Reloaded += LoadFromConfig;
    }

    public string[] ModelSizes { get; } =
        { "tiny", "base", "small", "medium", "large-v2", "large-v3" };
    public string[] Devices { get; } = { "auto", "cuda", "cpu" };
    public string[] ComputeTypes { get; } =
        { "auto", "float16", "int8", "int8_float16", "float32" };
    public string[] Languages { get; } = { "auto", "fr", "en", "es", "de", "it", "nl", "pt" };

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GeneralViewModel), nameof(ValidateDirectory))]
    private string _watchRoot = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GeneralViewModel), nameof(ValidateDirectory))]
    private string _outputRoot = "";

    [ObservableProperty]
    private string _modelCacheDir = "";

    [ObservableProperty]
    private string _modelSize = "large-v3";

    [ObservableProperty]
    private string _device = "auto";

    [ObservableProperty]
    private string _computeType = "auto";

    [ObservableProperty]
    private string _language = "auto";

    [ObservableProperty]
    private bool _autoRetryFailedJobs;

    [ObservableProperty]
    private int _maxAutoRetries = 3;

    [ObservableProperty]
    private bool _diarizationEnabled = true;

    [ObservableProperty]
    private bool _speakerIdEnabled;

    [ObservableProperty]
    private double _speakerThreshold = 0.55;

    /// <summary>Nombre de locuteurs attendu (0 = auto). Force min = max quand &gt; 0.</summary>
    [ObservableProperty]
    private int _speakerCount;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GeneralViewModel), nameof(ValidateToken))]
    private string _hfToken = "";

    [ObservableProperty]
    private bool _isTokenVisible;

    /// <summary>Revalide tous les champs et indique si la page est valide (pour la sauvegarde).</summary>
    public bool IsValid
    {
        get
        {
            ValidateAllProperties();
            return !HasErrors;
        }
    }

    // ---- Heures d'inactivite ----
    public ObservableCollection<QuietPeriodRow> QuietHours { get; } = new();

    [ObservableProperty]
    private QuietPeriodRow? _selectedQuietPeriod;

    [RelayCommand]
    private void AddQuietPeriod()
    {
        var p = new QuietPeriod
        {
            Days = new(),
            Start = "22:00",
            End = "06:00",
        };
        C.QuietHours.Add(p);
        var row = new QuietPeriodRow(p);
        QuietHours.Add(row);
        SelectedQuietPeriod = row;
    }

    [RelayCommand]
    private void RemoveQuietPeriod()
    {
        if (SelectedQuietPeriod is null)
            return;
        C.QuietHours.Remove(SelectedQuietPeriod.Model);
        QuietHours.Remove(SelectedQuietPeriod);
    }

    private void LoadFromConfig()
    {
        WatchRoot = C.WatchRoot;
        OutputRoot = C.OutputRoot;
        ModelCacheDir = C.ModelCacheDir;
        AutoRetryFailedJobs = C.AutoRetryFailedJobs;
        MaxAutoRetries = C.MaxAutoRetries;
        ModelSize = C.Engine.ModelSize;
        Device = C.Engine.Device;
        ComputeType = C.Engine.ComputeType;
        Language = C.Engine.Language;
        DiarizationEnabled = C.Diarization.Enabled;
        SpeakerCount =
            C.Diarization.MinSpeakers.HasValue
            && C.Diarization.MinSpeakers == C.Diarization.MaxSpeakers
                ? C.Diarization.MinSpeakers.Value
                : 0;
        SpeakerIdEnabled = C.SpeakerIdentification.Enabled;
        SpeakerThreshold = C.SpeakerIdentification.Threshold;
        HfToken = C.HfToken ?? "";

        QuietHours.Clear();
        foreach (var p in C.QuietHours)
            QuietHours.Add(new QuietPeriodRow(p));

        ValidateAllProperties();
    }

    // Ecriture live dans la config partagée.
    partial void OnWatchRootChanged(string value)
    {
        C.WatchRoot = value;
        QueueWatchRootCheck(value);
    }

    partial void OnOutputRootChanged(string value)
    {
        C.OutputRoot = value;
        QueueOutputRootCheck(value);
    }

    partial void OnModelCacheDirChanged(string value) => C.ModelCacheDir = value;

    partial void OnAutoRetryFailedJobsChanged(bool value) => C.AutoRetryFailedJobs = value;

    partial void OnMaxAutoRetriesChanged(int value) => C.MaxAutoRetries = value < 1 ? 1 : value;

    partial void OnModelSizeChanged(string value) => C.Engine.ModelSize = value;

    partial void OnDeviceChanged(string value) => C.Engine.Device = value;

    partial void OnComputeTypeChanged(string value) => C.Engine.ComputeType = value;

    partial void OnLanguageChanged(string value) => C.Engine.Language = value;

    partial void OnDiarizationEnabledChanged(bool value) => C.Diarization.Enabled = value;

    partial void OnSpeakerCountChanged(int value)
    {
        if (value <= 0)
        {
            C.Diarization.MinSpeakers = null;
            C.Diarization.MaxSpeakers = null;
        }
        else
        {
            C.Diarization.MinSpeakers = value;
            C.Diarization.MaxSpeakers = value;
        }
    }

    partial void OnSpeakerIdEnabledChanged(bool value) => C.SpeakerIdentification.Enabled = value;

    partial void OnSpeakerThresholdChanged(double value) =>
        C.SpeakerIdentification.Threshold = value;

    partial void OnHfTokenChanged(string value) =>
        C.HfToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private void BrowseWatch()
    {
        var p = Browse(WatchRoot);
        if (p != null)
            WatchRoot = p;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var p = Browse(OutputRoot);
        if (p != null)
            OutputRoot = p;
    }

    [RelayCommand]
    private void ToggleTokenVisibility() => IsTokenVisible = !IsTokenVisible;

    private static string? Browse(string current)
    {
        var dlg = new OpenFolderDialog { Title = "Choisir un dossier" };
        var expanded = ConfigStore.ExpandPath(current);
        if (!string.IsNullOrWhiteSpace(expanded) && Directory.Exists(expanded))
            dlg.InitialDirectory = expanded;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    // La validation synchrone reste VOLONTAIREMENT bon marche (non-vide seulement) : elle est
    // rejouee a chaque frappe. Le test d'existence (Directory.Exists) est deporte en tache de fond
    // debouncee (voir QueueDirectoryCheck) car sur un chemin reseau lent/indisponible il peut geler
    // l'UI plusieurs dizaines de secondes ; il alimente un simple avertissement, pas un blocage.
    public static ValidationResult? ValidateDirectory(string? path, ValidationContext _) =>
        string.IsNullOrWhiteSpace(path)
            ? new ValidationResult("Chemin requis.")
            : ValidationResult.Success;

    // ---- Avertissement d'existence (asynchrone, non bloquant) ----
    [ObservableProperty]
    private string _watchRootWarning = "";

    [ObservableProperty]
    private string _outputRootWarning = "";

    private CancellationTokenSource? _watchCheckCts;
    private CancellationTokenSource? _outputCheckCts;

    private void QueueWatchRootCheck(string value)
    {
        _watchCheckCts?.Cancel();
        _watchCheckCts = new CancellationTokenSource();
        _ = CheckDirectoryAsync(value, _watchCheckCts.Token, w => WatchRootWarning = w);
    }

    private void QueueOutputRootCheck(string value)
    {
        _outputCheckCts?.Cancel();
        _outputCheckCts = new CancellationTokenSource();
        _ = CheckDirectoryAsync(value, _outputCheckCts.Token, w => OutputRootWarning = w);
    }

    /// <summary>
    /// Verifie l'existence d'un dossier hors thread UI, apres un court debounce, et publie un
    /// avertissement via <paramref name="setWarning"/>. Le controle precedent est annule a chaque
    /// frappe (token). Ne bloque jamais la saisie, meme sur un chemin reseau lent/indisponible.
    /// </summary>
    private static async Task CheckDirectoryAsync(
        string value,
        CancellationToken token,
        Action<string> setWarning
    )
    {
        try
        {
            await Task.Delay(500, token);
            if (string.IsNullOrWhiteSpace(value))
            {
                setWarning("");
                return;
            }
            var expanded = ConfigStore.ExpandPath(value);
            var exists = await Task.Run(() => Directory.Exists(expanded), token);
            if (!token.IsCancellationRequested)
                setWarning(exists ? "" : "Dossier introuvable (il sera créé ou à corriger).");
        }
        catch (OperationCanceledException)
        { /* remplace par un controle plus recent : rien a faire */
        }
    }

    public static ValidationResult? ValidateToken(string? token, ValidationContext _)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ValidationResult.Success; // optionnel
        return token.StartsWith("hf_", StringComparison.Ordinal)
            ? ValidationResult.Success
            : new ValidationResult("Format attendu : hf_…");
    }
}
