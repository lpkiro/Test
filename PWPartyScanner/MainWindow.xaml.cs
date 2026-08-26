using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PWPartyScanner.Models;
using PWPartyScanner.Services;

namespace PWPartyScanner;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ScanResult> _results = new();
    private string[] _selectedFiles = Array.Empty<string>();
    private string[] _combinedPlayers = Array.Empty<string>();

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Оберіть скріншоти Perfect World",
            Filter = "Зображення|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Усі файли|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true) return;

        _selectedFiles = dlg.FileNames;
        _results.Clear();
        _combinedPlayers = Array.Empty<string>();
        CombinedPlayersList.ItemsSource = null;
        CombinedCountRun.Text = "0";
        CombinedExpander.Visibility = Visibility.Collapsed;

        ProcessButton.IsEnabled = _selectedFiles.Length > 0;
        ExportButton.IsEnabled = false;
        StatusText.Text = $"Обрано файлів: {_selectedFiles.Length}.";
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Length == 0) return;

        SelectButton.IsEnabled = false;
        ProcessButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        _results.Clear();
        CombinedExpander.Visibility = Visibility.Collapsed;

        try
        {
            using var ocr = new OcrService(AppContext.BaseDirectory);

            for (var i = 0; i < _selectedFiles.Length; i++)
            {
                var file = _selectedFiles[i];
                StatusText.Text = $"Опрацювання {i + 1}/{_selectedFiles.Length}: {Path.GetFileName(file)}";

                ScanResult result;
                try
                {
                    result = await Task.Run(() => ocr.Scan(file));
                }
                catch (Exception ex)
                {
                    result = new ScanResult
                    {
                        FileName = Path.GetFileName(file),
                        FilePath = file,
                        Status = "Помилка: " + ex.Message
                    };
                }

                _results.Add(result);
            }

            UpdateCombinedResult();
            ExportButton.IsEnabled = _results.Any(r => r.UniqueCount > 0);

            var mergeInfo = MergeScreenshotsCheckBox.IsChecked == true && _results.Count >= 2
                ? $" Разом унікальних по всіх: {_combinedPlayers.Length}."
                : string.Empty;

            StatusText.Text = $"Готово. Опрацьовано {_results.Count} файлів.{mergeInfo}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Помилка ініціалізації OCR.";
        }
        finally
        {
            SelectButton.IsEnabled = true;
            ProcessButton.IsEnabled = _selectedFiles.Length > 0;
        }
    }

    private void MergeScreenshotsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateCombinedResult();
    }

    private void UpdateCombinedResult()
    {
        var shouldMerge = MergeScreenshotsCheckBox.IsChecked == true && _results.Count >= 2;

        if (!shouldMerge)
        {
            _combinedPlayers = Array.Empty<string>();
            CombinedPlayersList.ItemsSource = null;
            CombinedCountRun.Text = "0";
            CombinedExpander.Visibility = Visibility.Collapsed;
            return;
        }

        _combinedPlayers = _results
            .SelectMany(r => r.UniquePlayers)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        CombinedPlayersList.ItemsSource = _combinedPlayers;
        CombinedCountRun.Text = _combinedPlayers.Length.ToString();
        CombinedExpander.Visibility = Visibility.Visible;
        CombinedExpander.IsExpanded = true;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Зберегти результат",
            Filter = "Text file|*.txt",
            FileName = "players_report.txt"
        };

        if (dlg.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        foreach (var item in _results)
        {
            sb.AppendLine($"Файл: {item.FileName}");
            sb.AppendLine($"Гравці поряд — рядків на скріні: {item.NearbyDetectedCount}");
            sb.AppendLine($"Гравці поряд — знайдено у players_list.txt: {item.NearbyCount}");
            foreach (var name in item.NearbyPlayers) sb.AppendLine($"  - {name}");

            sb.AppendLine($"Члени групи — рядків на скріні: {item.PartyDetectedCount}");
            sb.AppendLine($"Члени групи — знайдено у players_list.txt: {item.PartyCount}");
            foreach (var name in item.PartyPlayers) sb.AppendLine($"  - {name}");

            sb.AppendLine($"Унікальних зі списку гільдії: {item.UniqueCount}");
            foreach (var name in item.UniquePlayers) sb.AppendLine($"  - {name}");
            sb.AppendLine();
        }

        if (MergeScreenshotsCheckBox.IsChecked == true && _results.Count >= 2)
        {
            UpdateCombinedResult();
            sb.AppendLine("========================================");
            sb.AppendLine("РАЗОМ ПО ВСІХ СКРІНШОТАХ");
            sb.AppendLine($"Унікальних: {_combinedPlayers.Length}");
            foreach (var name in _combinedPlayers) sb.AppendLine($"  - {name}");
            sb.AppendLine();
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusText.Text = $"Збережено: {dlg.FileName}";
    }
}
