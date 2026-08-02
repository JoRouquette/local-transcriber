using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalTranscriber.Core.Configuration;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>
/// Ligne editable d'une plage d'inactivite. Enveloppe un <see cref="QuietPeriod"/> :
/// les modifications s'ecrivent directement dans le modele (donc dans la config).
/// </summary>
public sealed class QuietPeriodRow : ObservableObject
{
    public QuietPeriod Model { get; }

    public QuietPeriodRow(QuietPeriod model) => Model = model;

    /// <summary>Jours en texte : "mon,tue,..." (vide = tous les jours).</summary>
    public string DaysText
    {
        get => string.Join(",", Model.Days);
        set
        {
            Model.Days = (value ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.ToLowerInvariant())
                .ToList();
            OnPropertyChanged();
        }
    }

    public string Start
    {
        get => Model.Start;
        set { Model.Start = value; OnPropertyChanged(); }
    }

    public string End
    {
        get => Model.End;
        set { Model.End = value; OnPropertyChanged(); }
    }
}
