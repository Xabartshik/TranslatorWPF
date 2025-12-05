// Таблица идентификаторов на основе ДЕРЕВА областей видимости
// Каждая область может иметь множество дочерних областей (один родитель, множество потомков)
// Это более точно отражает структуру вложенности блоков в программе

using System;
using System.Collections.Generic;
using System.Linq;

namespace Parser;

/// <summary>
/// Запись в таблице идентификаторов (с поддержкой областей видимости и затенения).
/// </summary>
public class Entry
{
    public string Name { get; init; } = string.Empty; // Имя идентификатора
    public string Kind { get; set; } = "var";         // var / func / param / type / std
    public string Type { get; set; } = "int";         // Имя типа (int, float, bool, ...)
    public int ScopeDepth { get; set; } = -1;         // Глубина области (0=глобальная, 1=функция, 2=блок, ...)
    public Entry? Shadowed { get; set; }              // Кого затеняет
    public bool IsInitialized { get; set; } = false;  // Было ли присваивание
    public int LineDeclared { get; init; }            // Строка объявления
    public int ColumnDeclared { get; init; }          // Столбец объявления
    public bool IsConst { get; set; } = false;        // Константная?

    public Entry(string name, string kind, string type, int line, int column)
    {
        Name = name;
        Kind = kind;
        Type = type;
        LineDeclared = line;
        ColumnDeclared = column;
    }

    public override string ToString() =>
        $"{Name}:{Type} (kind={Kind}, depth={ScopeDepth}, const={IsConst}, init={IsInitialized})";
}

/// <summary>
/// УЗЕЛ дерева областей видимости.
/// Каждый узел может иметь множество дочерних областей (один родитель, много потомков).
/// Это представляет структуру вложения блоков, if/else, циклов и т.д.
/// </summary>
public sealed class ScopeNode
{
    /// <summary>Глубина области (0 = глобальная, увеличивается при вхождении)</summary>
    public int Depth { get; }

    /// <summary>Родительская область (null для глобальной)</summary>
    public ScopeNode? ParentScope { get; }

    /// <summary>Дочерние области (несколько if/else блоков, циклов и т.д.)</summary>
    public List<ScopeNode> ChildScopes { get; } = new();

    /// <summary>Идентификаторы, объявленные в этой области</summary>
    public Dictionary<string, Entry> Bindings { get; } = new();

    /// <summary>Метка для идентификации (например, "if-then", "while-body", "func-body main", "global")</summary>
    public string Label { get; set; }

    public ScopeNode(int depth, ScopeNode? parentScope, string label = "")
    {
        Depth = depth;
        ParentScope = parentScope;
        Label = label;
    }

    /// <summary>Поиск идентификатора в этой области и выше (вверх по дереву).</summary>
    public Entry? LookupInChain(string name)
    {
        var current = this;
        while (current != null)
        {
            if (current.Bindings.TryGetValue(name, out var entry))
                return entry;

            current = current.ParentScope;
        }

        return null;
    }

    /// <summary>Красивое отображение структуры дерева в консоль.</summary>
    public void PrintTree(int indent = 0)
    {
        // Схлопываем пустые анонимные области (без имени и без переменных, с одним потомком),
        // чтобы не было странных строк вида "[Scope Depth=1 ]".
        if (string.IsNullOrWhiteSpace(Label) &&
            Bindings.Count == 0 &&
            ChildScopes.Count == 1 &&
            ParentScope != null)
        {
            // Переходим сразу к единственному потомку, не печатая текущий узел.
            ChildScopes[0].PrintTree(indent);
            return;
        }

        string indentStr = new string(' ', indent * 2);
        string labelPart = string.IsNullOrWhiteSpace(Label) ? "" : $" {Label}";

        Console.WriteLine($"{indentStr}[Scope Depth={Depth}{labelPart}]");

        foreach (var binding in Bindings)
        {
            Console.WriteLine($"{indentStr} - {binding.Value}");
        }

        foreach (var child in ChildScopes)
        {
            child.PrintTree(indent + 1);
        }
    }
}

/// <summary>
/// Менеджер областей видимости + таблица идентификаторов на основе ДЕРЕВА.
/// </summary>
/// <remarks>
/// Отличие от стека:
/// - Стек: линейная последовательность (0 -> 1 -> 2 -> 1 -> 0)
/// - Дерево: каждая область может иметь МНОЖЕСТВО дочерних областей
///
/// Это правильнее отражает структуру программы:
/// - if (x > 0) { ... }  <- одна ветка
/// - else { ... }        <- другая ветка
/// - Обе вложены в одну функцию
/// </remarks>
public sealed class ScopeManager
{
    /// <summary>Корень дерева (глобальная область).</summary>
    private readonly ScopeNode _globalScope;

    /// <summary>Текущий узел (по которому мы ходим в дереве).</summary>
    private ScopeNode _currentScope;

    /// <summary>
    /// Стек текущих путей (для отслеживания пути от корня к текущему узлу).
    /// Нужен для восстановления пути при ExitScope.
    /// </summary>
    private readonly Stack<ScopeNode> _pathStack;

    public ScopeManager()
    {
        _globalScope = new ScopeNode(0, null, "global");
        _currentScope = _globalScope;
        _pathStack = new Stack<ScopeNode>();
        _pathStack.Push(_globalScope);
    }

    /// <summary>Текущая глубина вложения.</summary>
    public int CurrentDepth => _currentScope.Depth;

    /// <summary>
    /// Создать новую дочернюю область.
    /// Она становится текущей и добавляется в детей текущей области.
    /// </summary>
    public void EnterScope(string label = "")
    {
        var newScope = new ScopeNode(_currentScope.Depth + 1, _currentScope, label);
        _currentScope.ChildScopes.Add(newScope);
        _pathStack.Push(newScope);
        _currentScope = newScope;
    }

    /// <summary>Выйти из текущей области (подняться на уровень выше).</summary>
    public void ExitScope()
    {
        if (_pathStack.Count > 1)
        {
            _pathStack.Pop();
            _currentScope = _pathStack.Peek();
        }
    }

    /// <summary>Поиск идентификатора от текущей области вверх по цепочке родителей.</summary>
    public Entry? Lookup(string name) => _currentScope.LookupInChain(name);

    /// <summary>Объявить новый идентификатор в текущей области.</summary>
    public Entry Declare(
        string name,
        string kind,
        string type,
        bool isConst,
        int line,
        int column,
        IList<(int, int, string)> errors)
    {
        // Проверка на повторное объявление в ТЕКУЩЕЙ области
        if (_currentScope.Bindings.ContainsKey(name))
        {
            errors.Add((line, column,
                $"Повторное объявление идентификатора '{name}' в данной области видимости."));
            return _currentScope.Bindings[name];
        }

        // Поиск затеняемого идентификатора (во внешних областях)
        var shadowed = Lookup(name);

        var entry = new Entry(name, kind, type, line, column)
        {
            ScopeDepth = _currentScope.Depth,
            Shadowed = shadowed,
            IsConst = isConst,
            IsInitialized = kind == "std" || kind == "type"
        };

        _currentScope.Bindings[name] = entry;
        return entry;
    }

    /// <summary>Требование: идентификатор должен быть объявлен и инициализирован.</summary>
    public Entry? Require(string name, int line, int column, IList<(int, int, string)> errors)
    {
        var entry = Lookup(name);
        if (entry == null)
        {
            errors.Add((line, column, $"Использование необъявленного идентификатора '{name}'."));
            return null;
        }

        // Пропустить проверку инициализации для встроенных типов
        if (!entry.IsInitialized && entry.Kind != "std" && entry.Kind != "type")
        {
            errors.Add((line, column, $"Использование неинициализированной переменной '{name}'."));
        }

        return entry;
    }

    /// <summary>Вывод дерева областей в консоль (для отладки).</summary>
    public void PrintScopeTree()
    {
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║ ДЕРЕВО ОБЛАСТЕЙ ВИДИМОСТИ                                      ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

        _globalScope.PrintTree();

        Console.WriteLine();
    }

    /// <summary>Получить информацию о текущей области.</summary>
    public string GetCurrentScopeInfo() =>
        $"Scope: {_currentScope.Label} (Depth={_currentScope.Depth}, " +
        $"Variables={_currentScope.Bindings.Count}, Children={_currentScope.ChildScopes.Count})";

    /// <summary>Все идентификаторы в текущей области (не включая родительские).</summary>
    public IEnumerable<Entry> GetCurrentScopeBindings() => _currentScope.Bindings.Values;

    /// <summary>Все видимые идентификаторы от текущей области до глобальной.</summary>
    public IEnumerable<Entry> GetAllVisibleBindings()
    {
        var result = new Dictionary<string, Entry>();
        var current = _currentScope;

        while (current != null)
        {
            foreach (var binding in current.Bindings)
            {
                if (!result.ContainsKey(binding.Key))
                    result[binding.Key] = binding.Value;
            }

            current = current.ParentScope;
        }

        return result.Values;
    }

    /// <summary>Получить дерево областей в виде строки (для MainWindow / ScopeTextBox).</summary>
    public string GetScopeTreeAsString()
    {
        var sb = new System.Text.StringBuilder();
        AppendScopeNodeToString(_globalScope, 0, sb);
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendScopeNodeToString(ScopeNode node, int indent, System.Text.StringBuilder sb)
    {
        // Аналогичная "схлопывающая" логика, как в PrintTree:
        // пустые анонимные узлы без переменных и с одним потомком не печатаем.
        if (string.IsNullOrWhiteSpace(node.Label) &&
            node.Bindings.Count == 0 &&
            node.ChildScopes.Count == 1 &&
            node.ParentScope != null)
        {
            AppendScopeNodeToString(node.ChildScopes[0], indent, sb);
            return;
        }

        string indentStr = new string(' ', indent * 2);
        string labelPart = string.IsNullOrWhiteSpace(node.Label) ? "" : $" {node.Label}";

        sb.AppendLine($"{indentStr}[Scope Depth={node.Depth}{labelPart}]");

        foreach (var binding in node.Bindings)
        {
            sb.AppendLine($"{indentStr} - {binding.Value}");
        }

        foreach (var child in node.ChildScopes)
        {
            AppendScopeNodeToString(child, indent + 1, sb);
        }
    }
}
