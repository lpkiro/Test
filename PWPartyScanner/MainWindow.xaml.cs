using System.Collections.ObjectModel;
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

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
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

            ExportButton.IsEnabled = _results.Any(r => r.UniqueCount > 0);
            StatusText.Text = $"Готово. Опрацьовано {_results.Count} файлів.";
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
            sb.AppendLine($"Гравці поряд: {item.NearbyCount}");
            foreach (var name in item.NearbyPlayers) sb.AppendLine($"  - {name}");
            sb.AppendLine($"Члени групи: {item.PartyCount}");
            foreach (var name in item.PartyPlayers) sb.AppendLine($"  - {name}");
            sb.AppendLine($"Унікальних: {item.UniqueCount}");
            foreach (var name in item.UniquePlayers) sb.AppendLine($"  - {name}");
            sb.AppendLine();
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusText.Text = $"Збережено: {dlg.FileName}";
    }
}
