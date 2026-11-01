using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Backspace.Models;

namespace Backspace.ViewModels;

/// <summary>
/// Renders <see cref="MaintenanceCadence"/> enum values as the friendly
/// strings that show up in the Pruning Policy / Check Policy combo boxes.
/// Without this, Avalonia falls back to enum.ToString() and the
/// AfterEachBackup value reads as one ugly run-on word.
/// </summary>
public static class MaintenanceCadenceHumanizer
{
    public static readonly IValueConverter To = new Converter();

    private sealed class Converter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is MaintenanceCadence c ? Humanize(c) : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }

    public static string Humanize(MaintenanceCadence c) => c switch
    {
        MaintenanceCadence.AfterEachBackup => "After each backup",
        MaintenanceCadence.Daily           => "Daily",
        MaintenanceCadence.Weekly          => "Weekly",
        MaintenanceCadence.Monthly         => "Monthly",
        _                                   => c.ToString(),
    };
}
