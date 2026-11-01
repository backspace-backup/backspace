using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Backspace.ViewModels;

/// <summary>One set of parameters for manual and automatic restore checks.</summary>
public sealed partial class RestoreCheckOptionsViewModel : ObservableObject
{
    [ObservableProperty] private bool _full;
    [ObservableProperty] private decimal _percentage = 1;
    [ObservableProperty] private decimal _downloadGiB = 1;
    [ObservableProperty] private bool _compareOriginals;
    public RestoreCheckOptionsViewModel(TestRestoreOptions? options = null)
    {
        if (options is null) return;
        Full = options.Full; Percentage = (decimal)options.SamplePercent;
        DownloadGiB = options.MaximumSampleBytes / (1024m * 1024 * 1024);
        CompareOriginals = options.CompareOriginals;
    }
    public TestRestoreOptions ToOptions()
    {
        var options = new TestRestoreOptions(Full, (double)Percentage, (long)(DownloadGiB * 1024 * 1024 * 1024), CompareOriginals);
        options.Validate(); return options;
    }
}
