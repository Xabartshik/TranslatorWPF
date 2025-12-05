using System;
using System.Collections.Generic;

namespace Parser;

/// <summary>
/// Простейшая оптимизация мёртвого кода и избыточных присваиваний по АСТ.
/// Вызывается после парсера, принимает корень AST и возвращает новый (оптимизированный) AST.
/// </summary>
public sealed class AstOptimizer
{
    public AstNode Optimize(AstNode root)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        return OptimizeNode(root);
    }

    private AstNode OptimizeNode(AstNode node)
    {
        return node switch
        {
            ProgramNode p => OptimizeProgramNode(p),
            ExprStatementNode e => new ExprStatementNode(OptimizeNode(e.Expr)),
            AssignNode a => new AssignNode(
                                        OptimizeNode(a.Left),
                                        a.Op,
                                        OptimizeNode(a.Right)),
            BinaryNode b => new BinaryNode(
                                        b.Op,
                                        OptimizeNode(b.Left),
                                        OptimizeNode(b.Right)),
            UnaryNode u => new UnaryNode(u.Op, OptimizeNode(u.Operand)),
            // IdentifierNode / LiteralNode — листья
            _ => node
        };
    }

    private ProgramNode OptimizeProgramNode(ProgramNode program)
    {
        // 1) Рекурсивно оптимизируем детей
        var optimizedChildren = new List<AstNode>(program.Children.Count);
        foreach (var child in program.Children)
            optimizedChildren.Add(OptimizeNode(child));

        // 2) Удаляем код после return/break/continue
        optimizedChildren = EliminateDeadAfterTerminator(optimizedChildren);

        // 3) Устраняем избыточные присваивания
        optimizedChildren = EliminateRedundantAssignments(optimizedChildren);

        return new ProgramNode(optimizedChildren);
    }

    // ================= Мёртвый код после терминаторов =================

    private List<AstNode> EliminateDeadAfterTerminator(List<AstNode> stmts)
    {
        var result = new List<AstNode>(stmts.Count);
        foreach (var stmt in stmts)
        {
            result.Add(stmt);
            if (IsTerminator(stmt))
                break;
        }
        return result;
    }

    private static bool IsTerminator(AstNode node)
    {
        if (node is UnaryNode u && u.Op == "return")
            return true;

        if (node is LiteralNode lit &&
            lit.Kind == "keyword" &&
            (lit.Value == "break" || lit.Value == "continue"))
            return true;

        return false;
    }

    // ================= Избыточные присваивания =================

    private sealed class LastAssignInfo
    {
        public int Index;        // индекс оператора в списке stmts
        public bool UsedSince;   // было ли чтение переменной после этого присваивания
        public bool InDecl;      // это присваивание внутри объявления (decl) или нет
    }

    /// <summary>
    /// Локальная оптимизация:
    ///   x = v1;
    ///   ... (нет чтений x)
    ///   x = v2;
    /// →
    ///   x = v2;
    ///
    ///   int x = v1;
    ///   ... (нет чтений x)
    ///   x = v2;
    /// →
    ///   int x = v2;
    ///
    /// Область действия — только внутри одного ProgramNode, без захода в if/while/for.
    /// </summary>
    private List<AstNode> EliminateRedundantAssignments(List<AstNode> stmts)
    {
        var lastAssign = new Dictionary<string, LastAssignInfo>(StringComparer.Ordinal);
        var toRemove = new HashSet<int>();

        for (int i = 0; i < stmts.Count; i++)
        {
            var stmt = stmts[i];

            // 1) Оператор-выражение: возможно, присваивание
            if (stmt is ExprStatementNode es)
            {
                if (es.Expr is AssignNode assign && assign.Left is IdentifierNode id)
                {
                    // Чтения только из правой части
                    var reads = new HashSet<string>(StringComparer.Ordinal);
                    CollectIdentifierReads(assign.Right, reads);

                    MarkReads(reads, lastAssign);

                    string varName = id.Name;

                    if (lastAssign.TryGetValue(varName, out var prev))
                    {
                        if (!prev.UsedSince)
                        {
                            if (prev.InDecl)
                            {
                                // Предыдущее присваивание было в объявлении:
                                //   int x = v1;  ...  x = v2;
                                // Переносим v2 в объявление и удаляем текущий оператор.
                                TryRewriteDeclWithNewInit(stmts, prev.Index, varName, assign.Right);
                                toRemove.Add(i);
                            }
                            else
                            {
                                // Оба присваивания — обычные:
                                //   x = v1; ... x = v2;  →  удаляем v1
                                toRemove.Add(prev.Index);
                            }
                        }
                    }

                    // Текущее присваивание становится последним для этой переменной
                    lastAssign[varName] = new LastAssignInfo
                    {
                        Index = i,
                        UsedSince = false,
                        InDecl = false
                    };

                    continue;
                }
                else
                {
                    // Обычное выражение: ищем чтения
                    var reads = new HashSet<string>(StringComparer.Ordinal);
                    CollectIdentifierReads(es.Expr, reads);
                    MarkReads(reads, lastAssign);
                    continue;
                }
            }

            // 2) Объявление переменной с инициализацией:
            // BinaryNode("decl", typeNode, AssignNode(Id(name), "=", init))
            if (stmt is BinaryNode binDecl &&
                binDecl.Op == "decl" &&
                binDecl.Right is AssignNode declAssign &&
                declAssign.Left is IdentifierNode declId)
            {
                // Чтения только из правой части инициализации
                var reads = new HashSet<string>(StringComparer.Ordinal);
                CollectIdentifierReads(declAssign.Right, reads);
                MarkReads(reads, lastAssign);

                string varName = declId.Name;

                // Предыдущее присваивание той же переменной в этом блоке
                if (lastAssign.TryGetValue(varName, out var prev))
                {
                    // В нормальном коде это либо ошибка, либо новая область видимости.
                    // Для простоты просто "затираем" старую запись.
                    _ = prev;
                }

                lastAssign[varName] = new LastAssignInfo
                {
                    Index = i,
                    UsedSince = false,
                    InDecl = true
                };

                continue;
            }

            // 3) Любой другой оператор — барьер по управлению потоком
            lastAssign.Clear();
        }

        // Строим новый список без помеченных операторов
        var result = new List<AstNode>(stmts.Count - toRemove.Count);
        for (int i = 0; i < stmts.Count; i++)
        {
            if (!toRemove.Contains(i))
                result.Add(stmts[i]);
        }

        return result;
    }

    private static void MarkReads(
        HashSet<string> reads,
        Dictionary<string, LastAssignInfo> lastAssign)
    {
        foreach (var v in reads)
        {
            if (lastAssign.TryGetValue(v, out var info))
                info.UsedSince = true;
        }
    }

    /// <summary>
    /// Переписывает BinaryNode("decl", type, AssignNode(id, "=", oldInit))
    /// на BinaryNode("decl", type, AssignNode(id, "=", newInit)),
    /// если это действительно объявление указанной переменной.
    /// </summary>
    private static void TryRewriteDeclWithNewInit(
        List<AstNode> stmts,
        int index,
        string varName,
        AstNode newInit)
    {
        if (index < 0 || index >= stmts.Count)
            return;

        if (stmts[index] is not BinaryNode declBin ||
            declBin.Op != "decl" ||
            declBin.Right is not AssignNode declAssign ||
            declAssign.Left is not IdentifierNode declId ||
            declId.Name != varName)
        {
            return;
        }

        var updatedAssign = new AssignNode(
            declAssign.Left,
            declAssign.Op,
            newInit);

        stmts[index] = new BinaryNode(
            declBin.Op,
            declBin.Left,
            updatedAssign);
    }

    // ================= Сбор чтений идентификаторов =================

    /// <summary>
    /// Сбор всех идентификаторов (чтений переменных) в поддереве.
    /// Используется только для поиска чтений, поэтому
    /// не различает "значащие" и "незначащие" идентификаторы.
    /// </summary>
    private void CollectIdentifierReads(AstNode node, HashSet<string> reads)
    {
        switch (node)
        {
            case IdentifierNode id:
                reads.Add(id.Name);
                break;

            case LiteralNode:
                break;

            case ExprStatementNode es:
                CollectIdentifierReads(es.Expr, reads);
                break;

            case AssignNode a:
                // В общем случае чтения могут быть и в левом (например, a[i] = ...),
                // поэтому обходим оба подузла.
                CollectIdentifierReads(a.Left, reads);
                CollectIdentifierReads(a.Right, reads);
                break;

            case UnaryNode u:
                CollectIdentifierReads(u.Operand, reads);
                break;

            case BinaryNode b:
                CollectIdentifierReads(b.Left, reads);
                CollectIdentifierReads(b.Right, reads);
                break;

            case ProgramNode p:
                foreach (var ch in p.Children)
                    CollectIdentifierReads(ch, reads);
                break;

            default:
                break;
        }
    }
}
