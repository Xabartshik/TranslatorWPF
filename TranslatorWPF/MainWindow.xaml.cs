using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lexer;
using Parser;
using FlowchartGen;

namespace TranslatorWPF
{
    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public class ErrorItem
    {
        public string Message { get; set; }
        public string Location { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Error;
    }

    public class ErrorUnderlineAdorner : Adorner
    {
        private readonly TextBox _textBox;
        private readonly List<(int Line, int Column, int Length)> _errors;

        public ErrorUnderlineAdorner(TextBox textBox, List<(int Line, int Column, int Length)> errors)
            : base(textBox)
        {
            _textBox = textBox;
            _errors = errors ?? new List<(int, int, int)>();
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (_errors == null || _errors.Count == 0 || string.IsNullOrEmpty(_textBox.Text))
                return;

            var pen = new Pen(Brushes.Red, 1);
            pen.Freeze();

            foreach (var e in _errors)
            {
                if (e.Line <= 0 || e.Column <= 0)
                    continue;

                int charIndex = GetCharIndexFromLineColumn(e.Line, e.Column);
                if (charIndex < 0 || charIndex >= _textBox.Text.Length)
                    continue;

                Rect startRect = _textBox.GetRectFromCharacterIndex(charIndex);
                if (startRect.IsEmpty)
                    continue;

                int length = e.Length <= 0 ? 1 : e.Length;
                int endIndex = Math.Min(charIndex + length, _textBox.Text.Length);
                Rect endRect = _textBox.GetRectFromCharacterIndex(endIndex);

                double y = startRect.Bottom + 1;
                double x1 = startRect.Left;
                double x2 = endRect.IsEmpty ? startRect.Right + 6 : endRect.Left;

                DrawWavyLine(drawingContext, pen, x1, x2, y);
            }
        }

        private int GetCharIndexFromLineColumn(int line, int column)
        {
            int lineIndex = line - 1;
            if (lineIndex < 0 || lineIndex >= _textBox.LineCount)
                return -1;

            int lineStart = _textBox.GetCharacterIndexFromLineIndex(lineIndex);
            return lineStart + (column - 1);
        }

        private static void DrawWavyLine(DrawingContext dc, Pen pen, double x1, double x2, double y)
        {
            double step = 4;
            bool up = true;
            Point prev = new Point(x1, y);

            for (double x = x1; x < x2; x += step)
            {
                Point next = new Point(x + step, y + (up ? -2 : 2));
                dc.DrawLine(pen, prev, next);
                prev = next;
                up = !up;
            }
        }
    }

    public partial class MainWindow : Window
    {
        private string _currentFilePath;
        private bool _isWebViewInitialized;
        private ObservableCollection<ErrorItem> _errors;
        private string _currentMermaidCode;
        private AstNode _currentAst;
        private ScopeManager _currentScopes;

        private readonly List<(int Line, int Column, int Length)> _errorPositions = new();
        private AdornerLayer _errorAdornerLayer;
        private ErrorUnderlineAdorner _errorAdorner;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            _errors = new ObservableCollection<ErrorItem>();
            ErrorsListBox.ItemsSource = _errors;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
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

            UpdateLineNumbers();
            _errorAdornerLayer = AdornerLayer.GetAdornerLayer(CppCodeTextBox);
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

                DiagramView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                DiagramView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
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

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.WebMessageAsJson;

                if (message.Contains("\"type\":\"png\""))
                {
                    int dataStart = message.IndexOf("\"data\":\"") + 8;
                    int dataEnd = message.LastIndexOf("\"");
                    if (dataStart > 7 && dataEnd > dataStart)
                    {
                        string base64Data = message.Substring(dataStart, dataEnd - dataStart);
                        Dispatcher.Invoke(() => SavePngToFile(base64Data));
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show(
                        $"Ошибка обработки сообщения: {ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error));
            }
        }

        private void Translate_Click(object sender, RoutedEventArgs e)
        {
            ClearErrors();

            string cppCode = CppCodeTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cppCode))
            {
                AddError("Ошибка", "Введите C++ код", severity: DiagnosticSeverity.Error);
                ClearDiagramView();
                UpdateAstView(null);
                UpdateScopeView(null);
                UpdateTokensView(null);
                UpdateErrorAdorner();
                UpdateErrorCount();
                return;
            }

            try
            {
                var grammar = LanguageGrammarFactory.CreateCppGrammar();
                var lexer = new LexAnalyzer(cppCode, grammar);
                var tokens = lexer.Scan();

                // вывод списка лексем в таб "Tokens"
                UpdateTokensView(tokens);

                foreach (var error in lexer.Errors)
                {
                    AddError(
                        $"Лексер [{error.Line}:{error.Col}]",
                        error.Message,
                        error.Line,
                        error.Col,
                        severity: DiagnosticSeverity.Error);
                }

                var parser = new SyntaxAnalyzer(tokens);
                _currentScopes = parser._scopes;
                var ast = parser.ParseProgram();
                _currentAst = ast;

                UpdateAstView(ast);
                UpdateScopeView(_currentScopes);

                foreach (var error in parser.Errors)
                {
                    var msg = error.Message ?? string.Empty;
                    var severity =
                        msg.Contains("неинициализированной переменной", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("неинициализированной", StringComparison.OrdinalIgnoreCase)
                            ? DiagnosticSeverity.Warning
                            : DiagnosticSeverity.Error;

                    AddError(
                        $"Парсер [{error.Line}:{error.Col}]",
                        msg,
                        error.Line,
                        error.Col,
                        severity: severity);
                }

                if (ast == null)
                {
                    AddError("Критическая ошибка", "Не удалось разобрать программу", severity: DiagnosticSeverity.Error);
                    ClearDiagramView();
                    UpdateAstView(null);
                    UpdateScopeView(null);
                    UpdateErrorAdorner();
                    UpdateErrorCount();
                    return;
                }

                bool hasErrors = _errors.Any(ei => ei.Severity == DiagnosticSeverity.Error);

                if (hasErrors)
                {
                    ClearDiagramView();
                }
                else
                {
                    var flowchartGen = new ASTToMermaid();
                    string mermaidCode = flowchartGen.Generate(ast, "C++ Flowchart");
                    _currentMermaidCode = mermaidCode;

                    ShowDiagramView();
                    _ = UpdateDiagramAsync(mermaidCode);

                    AddError("Успех", "Программа успешно переведена в блок-схему", severity: DiagnosticSeverity.Info);
                }
            }
            catch (Exception ex)
            {
                AddError("Критическая ошибка", ex.Message, severity: DiagnosticSeverity.Error);
                ClearDiagramView();
                UpdateAstView(null);
                UpdateScopeView(null);
                UpdateTokensView(null);
            }

            UpdateErrorAdorner();
            UpdateErrorCount();
        }

        private async Task UpdateDiagramAsync(string mermaidCode)
        {
            if (!_isWebViewInitialized || DiagramView.CoreWebView2 == null)
                return;

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
                AddError(
                    "WebView2 ошибка",
                    $"Ошибка при отправке диаграммы: {ex.Message}",
                    severity: DiagnosticSeverity.Error);
            }
        }

        private void ClearDiagramView()
        {
            _currentMermaidCode = null;

            if (DiagramView != null)
            {
                DiagramView.Visibility = Visibility.Collapsed;
                if (_isWebViewInitialized && DiagramView.CoreWebView2 != null)
                {
                    _ = DiagramView.CoreWebView2.ExecuteScriptAsync(
                        "window.updateMermaid && window.updateMermaid('graph TD;');");
                }
            }
        }

        private void ShowDiagramView()
        {
            if (DiagramView != null)
            {
                DiagramView.Visibility = Visibility.Visible;
            }
        }

        private async void ExportPNG_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentMermaidCode))
            {
                MessageBox.Show(
                    "Сначала создайте диаграмму нажав Translate",
                    "Нет диаграммы",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!_isWebViewInitialized || DiagramView.CoreWebView2 == null)
            {
                MessageBox.Show(
                    "WebView2 не инициализирован",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            try
            {
                string script = "window.exportToPNG && window.exportToPNG();";
                await DiagramView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка экспорта PNG: {ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void SavePngToFile(string base64Data)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PNG files (*.png)|*.png|All files (*.*)|*.*",
                Title = "Сохранить диаграмму как PNG",
                FileName = "flowchart.png"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string cleanData = base64Data;
                    if (cleanData.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanData = cleanData.Substring("data:image/png;base64,".Length);
                    }

                    byte[] imageBytes = Convert.FromBase64String(cleanData);
                    File.WriteAllBytes(dlg.FileName, imageBytes);

                    MessageBox.Show(
                        $"Диаграмма сохранена в {dlg.FileName}",
                        "Успех",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Ошибка сохранения файла: {ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void ExportMermaid_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentMermaidCode))
            {
                MessageBox.Show(
                    "Сначала создайте диаграмму нажав Translate",
                    "Нет диаграммы",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Mermaid files (*.mmd)|*.mmd|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Сохранить код Mermaid",
                FileName = "flowchart.mmd"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, _currentMermaidCode, Encoding.UTF8);
                    MessageBox.Show(
                        $"Код Mermaid сохранён в {dlg.FileName}",
                        "Успех",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Ошибка сохранения файла: {ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void AddError(
            string title,
            string message,
            int line = 0,
            int column = 0,
            int length = 1,
            DiagnosticSeverity severity = DiagnosticSeverity.Error)
        {
            var item = new ErrorItem
            {
                Message = $"[{title}] {message}",
                Location = (line > 0 && column > 0)
                    ? $"Line {line}, Col {column}"
                    : DateTime.Now.ToString("HH:mm:ss"),
                Line = line,
                Column = column,
                Severity = severity
            };

            _errors.Add(item);

            if (severity == DiagnosticSeverity.Error && line > 0 && column > 0)
            {
                _errorPositions.Add((line, column, length <= 0 ? 1 : length));
            }
        }

        private void ClearErrors_Click(object sender, RoutedEventArgs e)
        {
            ClearErrors();
        }

        private void ClearErrors()
        {
            _errors.Clear();
            _errorPositions.Clear();
            UpdateErrorAdorner();
            UpdateErrorCount();
        }

        private void UpdateErrorCount()
        {
            int errorCount = _errors.Count(e => e.Severity == DiagnosticSeverity.Error);
            int warningCount = _errors.Count(e => e.Severity == DiagnosticSeverity.Warning);
            ErrorCountTextBlock.Text = $"{errorCount} error(s), {warningCount} warning(s)";
        }

        private void UpdateErrorAdorner()
        {
            if (_errorAdornerLayer == null)
                return;

            if (_errorAdorner != null)
            {
                _errorAdornerLayer.Remove(_errorAdorner);
                _errorAdorner = null;
            }

            if (_errorPositions.Count == 0)
                return;

            _errorAdorner = new ErrorUnderlineAdorner(
                CppCodeTextBox,
                new List<(int, int, int)>(_errorPositions));
            _errorAdornerLayer.Add(_errorAdorner);
        }

        private void CppCodeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateLineNumbers();

            if (_errorAdorner != null)
            {
                _errorAdorner.InvalidateVisual();
            }
        }

        private void CppCodeTextBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (LineNumberScrollViewer != null)
            {
                LineNumberScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            }
        }

        private void UpdateLineNumbers()
        {
            if (LineNumbersTextBlock == null || CppCodeTextBox == null)
                return;

            int lineCount = CppCodeTextBox.LineCount;
            if (lineCount <= 0)
            {
                LineNumbersTextBlock.Text = "1";
                return;
            }

            var sb = new StringBuilder();
            for (int i = 1; i <= lineCount; i++)
            {
                sb.AppendLine(i.ToString());
            }

            LineNumbersTextBlock.Text = sb.ToString();
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            CppCodeTextBox.Clear();
            _currentFilePath = null;
            _currentMermaidCode = null;
            _currentAst = null;
            _currentScopes = null;

            Title = "C++ to Flowchart - New";

            ClearDiagramView();
            ClearErrors();
            UpdateAstView(null);
            UpdateScopeView(null);
            UpdateTokensView(null);
            UpdateLineNumbers();
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
                    _currentMermaidCode = null;
                    _currentAst = null;
                    _currentScopes = null;

                    Title = "C++ to Flowchart - " + Path.GetFileName(_currentFilePath);

                    ClearDiagramView();
                    ClearErrors();
                    UpdateAstView(null);
                    UpdateScopeView(null);
                    UpdateTokensView(null);
                    UpdateLineNumbers();
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
                "Правая панель: блок-схема, AST, таблица идентификаторов, список лексем\n" +
                "Нижняя панель: лог ошибок и предупреждений\n\n" +
                "Принцип работы: C++ → Лексер → Парсер → AST → Mermaid\n" +
                "Стандарт ГОСТ 19.701-90 (ЕСПД)",
                "About",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void UpdateAstView(AstNode? root)
        {
            if (AstTextBox == null)
                return;

            AstTextBox.Text = AstPrinter.PrintDeepTreeToString(root);
        }

        private void UpdateScopeView(ScopeManager? scopes)
        {
            if (ScopeTextBox == null)
                return;

            if (scopes == null)
            {
                ScopeTextBox.Text = string.Empty;
                return;
            }

            ScopeTextBox.Text = scopes.GetScopeTreeAsString();
        }

        private void UpdateTokensView(IReadOnlyList<Token>? tokens)
        {
            if (TokensTextBox == null)
                return;

            if (tokens == null || tokens.Count == 0)
            {
                TokensTextBox.Text = string.Empty;
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Index  Type          Line  Col   Lexeme");
            sb.AppendLine("-----  ------------  ----  ----  ----------------");

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                string type = t.Type.ToString();
                string lexeme = t.Lexeme.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                sb.AppendLine(
                    $"{i,5}  {type,-12}  {t.Line,4}  {t.Column,4}  {lexeme}");
            }

            TokensTextBox.Text = sb.ToString();
        }
    }
}
