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

    public string[] ModelSizes { get; } = { "tiny", "base", "small", "medium", "large-v2", "large-v3" };
    public string[] Devices { get; } = { "auto", "cuda", "cpu" };
    public string[] ComputeTypes { get; } = { "auto", "float16", "int8", "int8_float16", "float32" };
    public string[] Languages { get; } = { "auto", "fr", "en", "es", "de", "it", "nl", "pt" };

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GeneralViewModel), nameof(ValidateDirectory))]
    private string _watchRoot = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GeneralViewModel), nameof(ValidateDirectory))]
    private string _outputRoot = "";

    [ObservableProperty] private string _modelCacheDir = "";
    [ObservableProperty] private string _modelSize = "large-v3";
    [ObservableProperty] private string _device = "auto";
    [ObservableProperty] private string _computeType = "auto";
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private bool _diarizationEnabled = true;
    [ObservableProperty] private bool _speakerIdEnabled;
    [ObservableProperty] private double _speakerThreshold = 0.55;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GeneralViewModel), nameof(ValidateToken))]
    private string _hfToken = "";

    [ObservableProperty] private bool _isTokenVisible;

    /// <summary>Revalide tous les champs et indique si la page est valide (pour la sauvegarde).</summary>
    public bool IsValid
    {
        get { ValidateAllProperties(); return !HasErrors; }
    }

    // ---- Heures d'inactivite ----
    public ObservableCollection<QuietPeriodRow> QuietHours { get; } = new();

    [ObservableProperty] private QuietPeriodRow? _selectedQuietPeriod;

    [RelayCommand]
    private void AddQuietPeriod()
    {
        var p = new QuietPeriod { Days = new(), Start = "22:00", End = "06:00" };
        C.QuietHours.Add(p);
        var row = new QuietPeriodRow(p);
        QuietHours.Add(row);
        SelectedQuietPeriod = row;
    }

    [RelayCommand]
    private void RemoveQuietPeriod()
    {
        if (SelectedQuietPeriod is null) return;
        C.QuietHours.Remove(SelectedQuietPeriod.Model);
        QuietHours.Remove(SelectedQuietPeriod);
    }

    private void LoadFromConfig()
    {
        WatchRoot = C.WatchRoot;
        OutputRoot = C.OutputRoot;
        ModelCacheDir = C.ModelCacheDir;
        ModelSize = C.Engine.ModelSize;
        Device = C.Engine.Device;
        ComputeType = C.Engine.ComputeType;
        Language = C.Engine.Language;
        DiarizationEnabled = C.Diarization.Enabled;
        SpeakerIdEnabled = C.SpeakerIdentification.Enabled;
        SpeakerThreshold = C.SpeakerIdentification.Threshold;
        HfToken = C.HfToken ?? "";

        QuietHours.Clear();
        foreach (var p in C.QuietHours) QuietHours.Add(new QuietPeriodRow(p));

        ValidateAllProperties();
    }

    // Ecriture live dans la config partagée.
    partial void OnWatchRootChanged(string value) => C.WatchRoot = value;
    partial void OnOutputRootChanged(string value) => C.OutputRoot = value;
    partial void OnModelCacheDirChanged(string value) => C.ModelCacheDir = value;
    partial void OnModelSizeChanged(string value) => C.Engine.ModelSize = value;
    partial void OnDeviceChanged(string value) => C.Engine.Device = value;
    partial void OnComputeTypeChanged(string value) => C.Engine.ComputeType = value;
    partial void OnLanguageChanged(string value) => C.Engine.Language = value;
    partial void OnDiarizationEnabledChanged(bool value) => C.Diarization.Enabled = value;
    partial void OnSpeakerIdEnabledChanged(bool value) => C.SpeakerIdentification.Enabled = value;
    partial void OnSpeakerThresholdChanged(double value) => C.SpeakerIdentification.Threshold = value;
    partial void OnHfTokenChanged(string value) => C.HfToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private void BrowseWatch() { var p = Browse(WatchRoot); if (p != null) WatchRoot = p; }

    [RelayCommand]
    private void BrowseOutput() { var p = Browse(OutputRoot); if (p != null) OutputRoot = p; }

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

    public static ValidationResult? ValidateDirectory(string? path, ValidationContext _)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ValidationResult("Chemin requis.");
        var expanded = ConfigStore.ExpandPath(path);
        return Directory.Exists(expanded) ? ValidationResult.Success : new ValidationResult("Dossier introuvable.");
    }

    public static ValidationResult? ValidateToken(string? token, ValidationContext _)
    {
        if (string.IsNullOrWhiteSpace(token)) return ValidationResult.Success; // optionnel
        return token.StartsWith("hf_", StringComparison.Ordinal)
            ? ValidationResult.Success
            : new ValidationResult("Format attendu : hf_…");
    }
}
