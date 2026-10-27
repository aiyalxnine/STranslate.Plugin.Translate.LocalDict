using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using STranslate.Plugin.Translate.LocalDict.Core;

namespace STranslate.Plugin.Translate.LocalDict.ViewModel;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IPluginContext _context;
    private readonly Settings _settings;
    private readonly DictionaryLibrary _library;

    public ObservableCollection<DictionaryItemViewModel> Dictionaries { get; } = [];

    public int[] ExampleCountOptions { get; } = [0, 3, 6, 10, 20];

    [ObservableProperty] public partial bool ShowEnglishDefinition { get; set; }
    [ObservableProperty] public partial bool ShowPhrasalVerbs { get; set; }
    [ObservableProperty] public partial int MaxExamples { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    public SettingsViewModel(IPluginContext context, Settings settings, DictionaryLibrary library)
    {
        _context = context;
        _settings = settings;
        _library = library;

        ShowEnglishDefinition = settings.ShowEnglishDefinition;
        ShowPhrasalVerbs = settings.ShowPhrasalVerbs;
        MaxExamples = settings.MaxExamples;

        lock (_settings)
        {
            foreach (var d in settings.Dictionaries)
                Dictionaries.Add(CreateItem(d.Path, d.Enabled));
        }
        IsEmpty = Dictionaries.Count == 0;
    }

    partial void OnShowEnglishDefinitionChanged(bool value)
    {
        _settings.ShowEnglishDefinition = value;
        _context.SaveSettingStorage<Settings>();
    }

    partial void OnShowPhrasalVerbsChanged(bool value)
    {
        _settings.ShowPhrasalVerbs = value;
        _context.SaveSettingStorage<Settings>();
    }

    partial void OnMaxExamplesChanged(int value)
    {
        _settings.MaxExamples = value;
        _context.SaveSettingStorage<Settings>();
    }

    [RelayCommand]
    private void Add()
    {
        var dialog = new OpenFileDialog
        {
            Title = Texts.Get(_context, "SelectDictionary"),
            Filter = "MDict (*.mdx)|*.mdx",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var file in dialog.FileNames)
        {
            if (Dictionaries.Any(d => d.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
            {
                _context.Snackbar.ShowWarning($"{Path.GetFileName(file)}：{Texts.Get(_context, "Duplicate")}");
                continue;
            }
            Dictionaries.Add(CreateItem(file, true));
        }
        Save();
    }

    [RelayCommand]
    private void Remove(DictionaryItemViewModel? item)
    {
        if (item == null) return;
        Dictionaries.Remove(item);
        Save();
    }

    [RelayCommand]
    private void MoveUp(DictionaryItemViewModel? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(DictionaryItemViewModel? item) => Move(item, 1);

    private void Move(DictionaryItemViewModel? item, int offset)
    {
        if (item == null) return;
        var index = Dictionaries.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Dictionaries.Count) return;
        Dictionaries.Move(index, target);
        Save();
    }

    private DictionaryItemViewModel CreateItem(string path, bool enabled)
    {
        var item = new DictionaryItemViewModel(path, enabled, Save);
        _ = item.LoadInfoAsync(_library, _context);
        return item;
    }

    private void Save()
    {
        lock (_settings)
        {
            _settings.Dictionaries = Dictionaries
                .Select(d => new DictionarySetting { Path = d.Path, Enabled = d.Enabled })
                .ToList();
        }
        IsEmpty = Dictionaries.Count == 0;
        _library.Retain(Dictionaries.Select(d => d.Path));
        _context.SaveSettingStorage<Settings>();
    }
}

public partial class DictionaryItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public string Path { get; }

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasError { get; set; }

    public DictionaryItemViewModel(string path, bool enabled, Action onChanged)
    {
        Path = path;
        Enabled = enabled;
        Title = System.IO.Path.GetFileNameWithoutExtension(path);
        _onChanged = onChanged;
    }

    partial void OnEnabledChanged(bool value) => _onChanged?.Invoke();

    public async Task LoadInfoAsync(DictionaryLibrary library, IPluginContext context)
    {
        Status = Texts.Get(context, "Loading");
        try
        {
            var dictionary = await library.GetAsync(Path);
            Title = dictionary.Title;
            var status = string.Format(Texts.Get(context, "EntryCount"), dictionary.EntryCount);
            status += "  ·  " + (dictionary.ResourceCount > 0
                ? string.Format(Texts.Get(context, "WithAudio"), dictionary.ResourceCount)
                : Texts.Get(context, "NoAudio"));
            Status = status;
            HasError = false;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            HasError = true;
        }
    }
}
