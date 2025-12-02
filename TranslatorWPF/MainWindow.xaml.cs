using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Lexer;
using Parser;
using FlowchartGen;

namespace TranslatorWPF
{
    public class ErrorItem
    {
        public string Message { get; set; }
        public string Location { get; set; }
    }

    public partial class MainWindow : Window
    {
        private string _currentFilePath;
        private bool _isWebViewInitialized;
        private ObservableCollection<ErrorItem> _errors;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            _errors = new ObservableCollection<ErrorItem>();
            ErrorsListBox.ItemsSource = _errors;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Пример начального кода
            CppCodeTextBox.Text = @"#include <iostream>
using namespace std;

int main() {
    int x = 0;
    int n = 10;
    
    while (x < n) {
        if (x % 2 == 0) {
            cout << x << endl;
        }
        x++;
    }
    
    return 0;
}";

            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
                if (!File.Exists(indexPath))
                {
                    MessageBox.Show(
                        "Файл wwwroot/index.html не найден. Создайте папку wwwroot и поместите туда index.html и mermaid.min.js.",
                        "Ошибка инициализации",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                DiagramView.Source = new Uri(indexPath, UriKind.Absolute);
                await DiagramView.EnsureCoreWebView2Async(null);
                _isWebViewInitialized = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка инициализации WebView2: {ex.Message}",
                    "WebView2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Translate_Click(object sender, RoutedEventArgs e)
        {
            ClearErrors();

            string cppCode = CppCodeTextBox.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cppCode))
            {
                AddError("Ошибка", "Введите C++ код");
                return;
            }

            try
            {
                // 1. Лексический анализ
                var grammar = LanguageGrammarFactory.CreateCppGrammar();
                var lexer = new LexAnalyzer(cppCode, grammar);
                var tokens = lexer.Scan();

                // Проверяем ошибки лексера
                foreach (var error in lexer.Errors)
                {
                    AddError($"Лексер [{error.Line}:{error.Col}]", error.Message);
                }

                // 2. Синтаксический анализ
                var parser = new SyntaxAnalyzer(tokens);
                var ast = parser.ParseProgram();

                // Проверяем ошибки парсера
                foreach (var error in parser.Errors)
                {
                    AddError($"Парсер [{error.Line}:{error.Col}]", error.Message);
                }

                if (ast == null)
                {
                    AddError("Критическая ошибка", "Не удалось разобрать программу");
                    return;
                }

                // 3. Генерация Mermaid-диаграммы
                var flowchartGen = new ASTToMermaid();
                string mermaidCode = flowchartGen.Generate(ast, "C++ Flowchart");

                // 4. Отправляем Mermaid в WebView2
                UpdateDiagramAsync(mermaidCode);

                if (_errors.Count == 0)
                {
                    AddError("Успех", "Программа успешно переведена в блок-схему");
                }
            }
            catch (Exception ex)
            {
                AddError("Критическая ошибка", ex.Message);
            }

            UpdateErrorCount();
        }

        private async Task UpdateDiagramAsync(string mermaidCode)
        {
            if (!_isWebViewInitialized || DiagramView.CoreWebView2 == null)
                return;

            // Экранируем для JS-строки
            string escaped = mermaidCode
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "")
                .Replace("\n", "\\n");

            string script = $"window.updateMermaid && window.updateMermaid(\"{escaped}\");";

            try
            {
                await DiagramView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                AddError("WebView2 ошибка", $"Ошибка при отправке диаграммы: {ex.Message}");
            }
        }

        private void AddError(string title, string message)
        {
            _errors.Add(new ErrorItem
            {
                Message = $"[{title}] {message}",
                Location = DateTime.Now.ToString("HH:mm:ss")
            });
        }

        private void ClearErrors_Click(object sender, RoutedEventArgs e)
        {
            ClearErrors();
        }

        private void ClearErrors()
        {
            _errors.Clear();
            UpdateErrorCount();
        }

        private void UpdateErrorCount()
        {
            ErrorCountTextBlock.Text = $"{_errors.Count} error(s)";
        }

        // --- Файловые операции ---

        private void New_Click(object sender, RoutedEventArgs e)
        {
            CppCodeTextBox.Clear();
            _currentFilePath = null;
            Title = "C++ to Flowchart - New";
            ClearErrors();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "C++ files (*.cpp;*.cc;*.cxx;*.h)|*.cpp;*.cc;*.cxx;*.h|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Открыть C++ файл"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    CppCodeTextBox.Text = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                    _currentFilePath = dlg.FileName;
                    Title = "C++ to Flowchart - " + Path.GetFileName(_currentFilePath);
                    ClearErrors();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Ошибка открытия файла: {ex.Message}",
                        "Ошибка файла",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFilePath == null)
            {
                SaveAs_Click(sender, e);
                return;
            }

            try
            {
                File.WriteAllText(_currentFilePath, CppCodeTextBox.Text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка сохранения файла: {ex.Message}",
                    "Ошибка файла",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "C++ files (*.cpp)|*.cpp|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Сохранить C++ файл",
                FileName = _currentFilePath ?? "program.cpp"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, CppCodeTextBox.Text, Encoding.UTF8);
                    _currentFilePath = dlg.FileName;
                    Title = "C++ to Flowchart - " + Path.GetFileName(_currentFilePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Ошибка сохранения файла: {ex.Message}",
                        "Ошибка файла",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "C++ to Flowchart Translator\n\n" +
                "Левая панель: ввод кода на C++\n" +
                "Правая панель: предпросмотр блок-схемы (Mermaid)\n" +
                "Нижняя панель: лог ошибок и предупреждений\n\n" +
                "Pipeline: Лексер → Парсер → AST → Mermaid\n" +
                "Стандарт ГОСТ 19.701-90 (ЕСПД)",
                "About",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}