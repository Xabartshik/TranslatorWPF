using System;
using System.Collections.Generic;
using System.Linq;
using Lexer;

namespace Parser;

/// <summary>
/// Синтаксический анализатор для подмножества языка C++.
/// Преобразует последовательность токенов в AST, выполняет базовую проверку типов и областей видимости.
/// </summary>
public sealed class SyntaxAnalyzer
{
    private readonly List<Token> _tokenList;
    private int _pos;
    private Token _lookahead;
    private bool _inErrorRecovery = false;

    public readonly ScopeManager _scopes = new();
    public List<(int Line, int Col, string Message)> Errors { get; } = new();

    public SyntaxAnalyzer(IEnumerable<Token> tokens)
    {
        if (tokens == null) throw new ArgumentNullException(nameof(tokens));

        _tokenList = tokens
            .Where(t => t.Type != TokenType.Whitespace && t.Type != TokenType.Comment)
            .ToList();

        _pos = 0;
        _lookahead = _pos < _tokenList.Count
            ? _tokenList[_pos]
            : new Token(TokenType.EndOfFile, string.Empty, -1, -1);
    }

    // ================= Поток токенов =================

    private void MoveNext()
    {
        if (_pos < _tokenList.Count)
            _pos++;

        _lookahead = _pos < _tokenList.Count
            ? _tokenList[_pos]
            : new Token(TokenType.EndOfFile, string.Empty, -1, -1);
    }

    private bool Is(TokenType type) => _lookahead.Type == type;

    private Token Consume()
    {
        var t = _lookahead;
        MoveNext();
        return t;
    }

    private Token Expect(TokenType type, string message)
    {
        if (Is(type))
            return Consume();

        if (!_inErrorRecovery)
        {
            _inErrorRecovery = true;
            Errors.Add((_lookahead.Line, _lookahead.Column, message));
        }

        return _lookahead;
    }

    private bool IsAtEnd() => _lookahead.Type == TokenType.EndOfFile;

    private void SkipToSemicolon()
    {
        while (_lookahead.Type != TokenType.Semicolon &&
               _lookahead.Type != TokenType.EndOfFile &&
               _lookahead.Type != TokenType.RBrace &&
               _lookahead.Type != TokenType.RParen)
        {
            MoveNext();
        }

        if (Is(TokenType.Semicolon))
            Consume();
    }

    private void SkipTo(TokenType target1, TokenType target2 = TokenType.EndOfFile)
    {
        while (_lookahead.Type != target1 &&
               _lookahead.Type != target2 &&
               _lookahead.Type != TokenType.LBrace &&
               _lookahead.Type != TokenType.RBrace &&
               _lookahead.Type != TokenType.EndOfFile)
        {
            MoveNext();
        }
    }

    private bool IsKeywordLike(TokenType type) =>
        type switch
        {
            TokenType.TypeInt or TokenType.TypeFloat or TokenType.TypeDouble or
            TokenType.TypeChar or TokenType.TypeBool or TokenType.TypeVoid or
            TokenType.TypeLong or TokenType.TypeShort or TokenType.TypeUnsigned or
            TokenType.TypeSigned or TokenType.TypeAuto or TokenType.TypeString or
            TokenType.TypeVector or TokenType.KeywordConst or
            TokenType.KeywordIf or TokenType.KeywordElse or
            TokenType.KeywordFor or TokenType.KeywordWhile or
            TokenType.KeywordDo or TokenType.KeywordReturn or
            TokenType.KeywordBreak or TokenType.KeywordContinue or
            TokenType.KeywordSwitch or TokenType.KeywordCase or
            TokenType.KeywordDefault or TokenType.KeywordNew or
            TokenType.KeywordDelete => true,
            _ => false
        };

    private void SkipUntilSemicolonOrNewStatement()
    {
        while (!IsAtEnd() &&
               _lookahead.Type != TokenType.Semicolon &&
               !IsKeywordLike(_lookahead.Type))
        {
            MoveNext();
        }

        if (Is(TokenType.Semicolon))
            Consume();
    }

    // ================= Точка входа =================

    public ProgramNode? ParseProgram()
    {
        try
        {
            return ParseCppProgram();
        }
        catch (Exception ex)
        {
            Errors.Add((-1, -1, $"Критическая ошибка парсера: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Проверка, что в текущей позиции начинается определение int main(...).
    /// Используется только как lookahead, состояние парсера не изменяет.
    /// </summary>
    private bool LooksLikeMainDefinition()
    {
        int idx = _pos;
        if (idx >= _tokenList.Count) return false;
        if (_tokenList[idx].Type != TokenType.TypeInt) return false;

        idx++; // после int

        // Указатели к main (int* main) допускаем синтаксически, но дальше можно ужесточить при желании.
        while (idx < _tokenList.Count && _tokenList[idx].Type == TokenType.OpMultiply)
            idx++;

        if (idx >= _tokenList.Count) return false;
        var nameTok = _tokenList[idx];
        if (nameTok.Type != TokenType.Identifier ||
            !string.Equals(nameTok.Lexeme, "main", StringComparison.Ordinal))
            return false;

        idx++;
        if (idx >= _tokenList.Count) return false;

        return _tokenList[idx].Type == TokenType.LParen;
    }

    /// <summary>
    /// Жёсткое требование: возможны только препроцессоры/using/пустые строки до int main(),
    /// затем одна функция int main(...), и никакого кода после неё.
    /// При нарушении добавляется ошибка и парсер останавливается.
    /// </summary>
    private ProgramNode ParseCppProgram()
    {
        var stmts = new List<AstNode>();

        // 1. Пропускаем разрешённые конструкции до возможного main.
        while (!IsAtEnd())
        {
            // Препроцессор
            if (_lookahead.Type == TokenType.Preprocessor)
            {
                Consume();
                while (Is(TokenType.Identifier) || Is(TokenType.OpLess) || Is(TokenType.OpGreater))
                    Consume();
                continue;
            }

            // Пустые/лишние ; и } (на всякий случай)
            if (Is(TokenType.Semicolon) || Is(TokenType.RBrace))
            {
                Consume();
                continue;
            }

            // using ...
            if (Is(TokenType.KeywordUsing))
            {
                ParseUsingNamespace();
                continue;
            }

            // Всё остальное — потенциальный код верхнего уровня
            break;
        }

        // Если файл закончился, а main так и не встретился
        if (IsAtEnd())
        {
            Errors.Add((-1, -1, "Ожидалась функция 'int main()' как точка входа программы."));
            return new ProgramNode(stmts);
        }

        // 2. В этой точке обязано начинаться int main(...), всё остальное — код вне main
        if (!LooksLikeMainDefinition())
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                "Код вне функции 'int main()' не допускается. Переместите весь код внутрь 'main'."));

            // Останавливаем парсер: аккуратно доедаем все токены
            while (!IsAtEnd())
                MoveNext();

            return new ProgramNode(stmts);
        }

        // 3. Разбираем определение main существующей логикой
        var typeTok = Consume(); // int
        SkipCppTemplateArguments();

        string pointerPrefix = "";
        while (Is(TokenType.OpMultiply))
        {
            pointerPrefix += "*";
            Consume();
        }

        var nameTok = Consume(); // main
        var mainFunc = ParseCppFunctionDeclaration(typeTok, nameTok, pointerPrefix);
        stmts.Add(mainFunc);

        // 4. Любые токены после main считаются кодом вне main
        if (!IsAtEnd())
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                "Код вне функции 'int main()' не допускается. Переместите весь код внутрь 'main'."));

            while (!IsAtEnd())
                MoveNext();
        }

        return new ProgramNode(stmts);
    }

    private void ParseUsingNamespace()
    {
        Consume(); // using

        if (Is(TokenType.KeywordNamespace))
        {
            Consume(); // namespace

            if (_lookahead.Type == TokenType.Identifier || _lookahead.Type == TokenType.StdNamespace)
            {
                var nsTok = Consume();
                var ns = nsTok.Lexeme;
                _ = ns;
            }
            else
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    "Ожидалось имя пространства имён после 'namespace'."));
            }

            if (!Is(TokenType.Semicolon))
                Errors.Add((_lookahead.Line, _lookahead.Column, "Ожидалась ';' после using namespace."));
            else
                Consume();
        }
        else
        {
            // using std::cout;
            while (!Is(TokenType.Semicolon) && _lookahead.Type != TokenType.EndOfFile)
                Consume();

            if (Is(TokenType.Semicolon))
                Consume();
        }
    }

    // ================= Объявления / операторы =================

    private bool IsCppType(TokenType type) =>
        type switch
        {
            TokenType.TypeInt or TokenType.TypeFloat or TokenType.TypeDouble or
            TokenType.TypeChar or TokenType.TypeBool or TokenType.TypeVoid or
            TokenType.TypeLong or TokenType.TypeShort or TokenType.TypeUnsigned or
            TokenType.TypeSigned or TokenType.TypeAuto or TokenType.TypeString or
            TokenType.TypeVector => true,
            _ => false
        };

    /// <summary>
    /// Точка ветвления: либо объявление (в т.ч. с const), либо обычный оператор.
    /// Здесь добавлена поддержка списка переменных: int a, b, c = 10;
    /// </summary>
    private AstNode? ParseCppStatementOrDeclaration()
    {
        bool isConst = false;
        int constLine = -1, constCol = -1;

        if (Is(TokenType.KeywordConst))
        {
            var constToken = Consume();
            isConst = true;
            constLine = constToken.Line;
            constCol = constToken.Column;
        }

        if (!IsCppType(_lookahead.Type))
        {
            if (isConst)
                Errors.Add((constLine, constCol, "Ожидался тип после 'const'."));
            return ParseCppStatement();
        }

        var typeTok = Consume();
        SkipCppTemplateArguments();

        // указатели: int*, int**
        string pointerPrefix = "";
        while (Is(TokenType.OpMultiply))
        {
            pointerPrefix += "*";
            Consume();
        }

        if (_lookahead.Type != TokenType.Identifier)
        {
            Errors.Add((_lookahead.Line, _lookahead.Column, "Ожидался идентификатор после типа."));
            return null;
        }

        var firstNameTok = Consume();

        // Функция: int f(...)
        if (Is(TokenType.LParen))
        {
            if (isConst)
                Errors.Add((constLine, constCol, "'const' не может использоваться для объявления функции."));
            return ParseCppFunctionDeclaration(typeTok, firstNameTok, pointerPrefix);
        }

        // Объявление переменных (возможно, нескольких через запятую)
        return ParseCppVarDeclList(typeTok, firstNameTok, isConst, pointerPrefix, requireSemicolon: true);
    }

    /// <summary>
    /// Общий разбор одной переменной после уже прочитанного типа и имени.
    /// Используется и в обычных объявлениях, и в инициализации for.
    /// </summary>
    private AstNode ParseSingleCppDeclarator(
        Token typeTok,
        Token nameTok,
        bool isConst,
        string pointerPrefix)
    {
        string varName = nameTok.Lexeme;
        string fullType = typeTok.Lexeme + pointerPrefix;

        // Массивы: int a[10][20]
        string arrayPostfix = "";
        while (Is(TokenType.LBracket))
        {
            Consume(); // [

            arrayPostfix += "[";

            if (!Is(TokenType.RBracket))
            {
                // размер массива можно игнорировать в типе, но выражение парсим
                var _ = ParseCppExpr();
                arrayPostfix += "]";
            }
            else
            {
                arrayPostfix += "]";
            }

            if (!Is(TokenType.RBracket))
                Errors.Add((_lookahead.Line, _lookahead.Column, "Ожидалась ']' в объявлении массива."));
            else
                Consume(); // ]
        }

        fullType += arrayPostfix;

        _scopes.Declare(
            varName,
            kind: "var",
            type: fullType,
            isConst: isConst,
            line: nameTok.Line,
            column: nameTok.Column,
            errors: Errors);

        var idNode = new IdentifierNode(varName, nameTok.Line, nameTok.Column);
        var typeNode = new IdentifierNode(typeTok.Lexeme + pointerPrefix, typeTok.Line, typeTok.Column);

        AstNode? init = null;
        if (Is(TokenType.OpAssign))
        {
            Consume(); // =
            init = ParseCppExpr();

            string exprType = GetExpressionType(init);
            if (!AreTypesCompatible(fullType, exprType))
            {
                Errors.Add((nameTok.Line, nameTok.Column,
                    $"Ошибка типов: несовместимые типы в инициализации {varName} ({fullType}) = ... ({exprType})"));
            }

            var entry = _scopes.Lookup(varName);
            if (entry != null)
                entry.IsInitialized = true;
        }

        if (isConst && init == null)
        {
            Errors.Add((nameTok.Line, nameTok.Column,
                $"Константная переменная '{varName}' должна быть инициализирована."));
        }

        var assign = new AssignNode(
            idNode,
            "=",
            init ?? new LiteralNode("void", string.Empty, nameTok.Line, nameTok.Column));

        return new BinaryNode("decl", typeNode, assign);
    }

    /// <summary>
    /// int a, b, c = 10; — список объявлений в операторе (с точкой с запятой).
    /// Возвращает либо один BinaryNode("decl", ...), либо ProgramNode из нескольких.
    /// </summary>
    private AstNode ParseCppVarDeclList(
        Token typeTok,
        Token firstNameTok,
        bool isConst,
        string pointerPrefix,
        bool requireSemicolon)
    {
        var decls = new List<AstNode>();

        // Первый декларатор уже имеет имя
        decls.Add(ParseSingleCppDeclarator(typeTok, firstNameTok, isConst, pointerPrefix));

        // Остальные через запятую
        while (Is(TokenType.Comma))
        {
            Consume(); // ,
            if (_lookahead.Type != TokenType.Identifier)
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    "Ожидался идентификатор после ',' в объявлении переменной."));
                break;
            }

            var nextName = Consume();
            decls.Add(ParseSingleCppDeclarator(typeTok, nextName, isConst, pointerPrefix));
        }

        if (requireSemicolon)
        {
            if (!Is(TokenType.Semicolon))
            {
                var firstVar = firstNameTok.Lexeme;
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' после объявления переменной(ых) '{firstVar}' — обнаружена '{_lookahead.Lexeme}'"));
                SkipToSemicolon();
            }
            else
            {
                Consume();
            }
        }

        return decls.Count == 1
            ? decls[0]
            : new ProgramNode(decls);
    }

    /// <summary>
    /// Вариант для инициализации в заголовке for: int i = 0, j = 1
    /// Точка с запятой обрабатывается отдельно в ParseCppForStatement().
    /// </summary>
    private AstNode ParseCppVarDeclForInit(
        Token typeTok,
        Token firstNameTok,
        bool isConst,
        string pointerPrefix)
    {
        return ParseCppVarDeclList(typeTok, firstNameTok, isConst, pointerPrefix, requireSemicolon: false);
    }

    private void SkipCppTemplateArguments()
    {
        if (!Is(TokenType.OpLess))
            return;

        int depth = 0;
        while (_lookahead.Type != TokenType.EndOfFile)
        {
            if (Is(TokenType.OpLess))
            {
                depth++;
                Consume();
            }
            else if (Is(TokenType.OpGreater))
            {
                depth--;
                Consume();
                if (depth == 0)
                    break;
            }
            else
            {
                Consume();
            }
        }
    }

    private AstNode ParseCppFunctionDeclaration(Token typeToken, Token nameToken, string pointerPrefix)
    {
        _scopes.Declare(
            nameToken.Lexeme,
            kind: "func",
            type: typeToken.Lexeme + pointerPrefix,
            isConst: false,
            line: nameToken.Line,
            column: nameToken.Column,
            errors: Errors);

        var funcEntry = _scopes.Lookup(nameToken.Lexeme);
        if (funcEntry != null)
            funcEntry.IsInitialized = false;

        var typeNode = new IdentifierNode(typeToken.Lexeme + pointerPrefix, typeToken.Line, typeToken.Column);
        var nameNode = new IdentifierNode(nameToken.Lexeme, nameToken.Line, nameToken.Column);

        // Параметры (просто пропускаем до закрывающей скобки)
        Consume(); // (
        int depth = 1;
        while (_lookahead.Type != TokenType.EndOfFile && depth > 0)
        {
            if (_lookahead.Type == TokenType.LParen) depth++;
            else if (_lookahead.Type == TokenType.RParen) depth--;

            if (depth > 0)
                Consume();
        }

        Expect(TokenType.RParen, "Ожидалась ')' в объявлении функции.");

        AstNode body;
        if (Is(TokenType.LBrace))
        {
            _scopes.EnterScope("func-body " + nameToken.Lexeme);
            body = ParseCppBlock();
            _scopes.ExitScope();
        }
        else
        {
            Errors.Add((nameToken.Line, nameToken.Column,
                "Предупреждение: отсутствует '{' после объявления функции — предполагается тело."));

            _scopes.EnterScope("func-body " + nameToken.Lexeme);

            var implicitStmts = new List<AstNode>();
            while (_lookahead.Type != TokenType.EndOfFile &&
                   !Is(TokenType.KeywordReturn))
            {
                var stmt = ParseCppStatementOrDeclaration();
                if (stmt != null)
                    implicitStmts.Add(stmt);
                else
                    SkipToSemicolon();
            }

            if (Is(TokenType.KeywordReturn))
                implicitStmts.Add(ParseCppStatement() ?? new LiteralNode("void", string.Empty, -1, -1));

            body = new ProgramNode(implicitStmts);
            _scopes.ExitScope();
        }

        return new BinaryNode("func-def", typeNode,
            new BinaryNode("func-params-body", nameNode, body));
    }

    private AstNode? ParseCppStatement()
    {
        // Объявления
        if (Is(TokenType.KeywordConst) || IsCppType(_lookahead.Type))
            return ParseCppStatementOrDeclaration();

        // if / while / do / for / block
        if (Is(TokenType.KeywordIf)) return ParseCppIfStatement();
        if (Is(TokenType.KeywordWhile)) return ParseCppWhileStatement();
        if (Is(TokenType.KeywordDo)) return ParseCppDoWhileStatement();
        if (Is(TokenType.KeywordFor)) return ParseCppForStatement();
        if (Is(TokenType.LBrace)) return ParseCppBlock();

        // break
        if (Is(TokenType.KeywordBreak))
        {
            var tok = Consume();
            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' после break — обнаружена '{_lookahead.Lexeme}'"));
                SkipToSemicolon();
            }
            else
            {
                Consume();
            }

            return new LiteralNode("keyword", "break", tok.Line, tok.Column);
        }

        // continue
        if (Is(TokenType.KeywordContinue))
        {
            var tok = Consume();
            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' после continue — обнаружена '{_lookahead.Lexeme}'"));
                SkipToSemicolon();
            }
            else
            {
                Consume();
            }

            return new LiteralNode("keyword", "continue", tok.Line, tok.Column);
        }

        // return
        if (Is(TokenType.KeywordReturn))
        {
            var retTok = Consume();
            AstNode expr = Is(TokenType.Semicolon)
                ? new LiteralNode("void", string.Empty, retTok.Line, retTok.Column)
                : ParseCppExpr();

            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' после return — обнаружена '{_lookahead.Lexeme}'"));
                SkipToSemicolon();
            }
            else
            {
                Consume();
            }

            return new UnaryNode("return", expr);
        }

        // delete / delete[]
        if (Is(TokenType.KeywordDelete))
        {
            var delTok = Consume();
            bool isArray = false;

            if (Is(TokenType.LBracket))
            {
                isArray = true;
                Consume();
                Expect(TokenType.RBracket, "Ожидалась ']' после delete.");
            }

            var expr = ParseCppExpr();

            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' после delete — обнаружена '{_lookahead.Lexeme}'"));
                SkipToSemicolon();
            }
            else
            {
                Consume();
            }

            return new UnaryNode(isArray ? "delete[]" : "delete", expr);
        }

        // Обычное выражение
        var e = ParseCppExpr();
        if (!Is(TokenType.Semicolon))
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                $"Ожидалась ';' после выражения — обнаружена '{_lookahead.Lexeme}'"));
            SkipToSemicolon();
        }
        else
        {
            Consume();
        }

        return new ExprStatementNode(e);
    }

    // ================= if / while / do / for / block =================

    private AstNode ParseCppIfStatement()
    {
        Consume(); // if

        AstNode condition;
        if (Is(TokenType.LParen))
        {
            Consume();
            condition = ParseCppExpr();

            if (!Is(TokenType.RParen))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ')' после условия if — обнаружена '{_lookahead.Lexeme}'"));
                SkipTo(TokenType.LBrace, TokenType.Semicolon);
            }
            else
            {
                Consume();
            }
        }
        else
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                $"Ожидалась '(' после if — обнаружена '{_lookahead.Lexeme}'"));
            condition = ParseCppExpr();
            SkipTo(TokenType.LBrace, TokenType.Semicolon);
        }

        _scopes.EnterScope("if-then");
        var thenBranch = ParseCppStatementOrBlock();
        _scopes.ExitScope();

        AstNode? elseBranch = null;
        if (Is(TokenType.KeywordElse))
        {
            Consume();
            _scopes.EnterScope("if-else");
            elseBranch = ParseCppStatementOrBlock();
            _scopes.ExitScope();
        }

        return new BinaryNode("if", condition,
            new BinaryNode("then-else", thenBranch,
                elseBranch ?? new LiteralNode("void", string.Empty, -1, -1)));
    }

    private AstNode ParseCppWhileStatement()
    {
        Consume(); // while

        AstNode condition;
        if (Is(TokenType.LParen))
        {
            Consume();
            condition = ParseCppExpr();

            if (!Is(TokenType.RParen))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ')' после условия while — обнаружена '{_lookahead.Lexeme}'"));
                SkipTo(TokenType.LBrace, TokenType.Semicolon);
            }
            else
            {
                Consume();
            }
        }
        else
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                $"Ожидалась '(' после while — обнаружена '{_lookahead.Lexeme}'"));
            condition = ParseCppExpr();
            SkipTo(TokenType.LBrace, TokenType.Semicolon);
        }

        var body = ParseCppStatementOrBlock();
        return new BinaryNode("while", condition, body);
    }

    private AstNode ParseCppDoWhileStatement()
    {
        Consume(); // do
        var body = ParseCppStatementOrBlock();

        if (!Is(TokenType.KeywordWhile))
        {
            Errors.Add((_lookahead.Line, _lookahead.Column, "Ожидалась 'while' в конце do-while."));
        }
        else
        {
            Consume();
        }

        Expect(TokenType.LParen, "Ожидалась '(' после while.");
        var condition = ParseCppExpr();
        Expect(TokenType.RParen, "Ожидалась ')' в условии do-while.");
        Expect(TokenType.Semicolon, "Ожидалась ';' в конце do-while.");

        return new BinaryNode("do-while", condition, body);
    }

    private AstNode ParseCppForStatement()
    {
        Consume(); // for

        if (!Is(TokenType.LParen))
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                $"Ожидалась '(' после for — обнаружена '{_lookahead.Lexeme}'"));
            return new LiteralNode("error", "for", _lookahead.Line, _lookahead.Column);
        }

        Consume(); // (
        _scopes.EnterScope("for-init");

        // init
        AstNode init;
        if (Is(TokenType.Semicolon))
        {
            init = new LiteralNode("void", string.Empty, -1, -1);
            Consume();
        }
        else if (Is(TokenType.KeywordConst) || IsCppType(_lookahead.Type))
        {
            bool isConst = false;
            if (Is(TokenType.KeywordConst))
            {
                var c = Consume();
                isConst = true;
                _ = c;
            }

            if (IsCppType(_lookahead.Type))
            {
                var typeTok = Consume();
                SkipCppTemplateArguments();

                string pointerPrefix = "";
                while (Is(TokenType.OpMultiply))
                {
                    pointerPrefix += "*";
                    Consume();
                }

                if (_lookahead.Type == TokenType.Identifier)
                {
                    var nameTok = Consume();
                    init = ParseCppVarDeclForInit(typeTok, nameTok, isConst, pointerPrefix);
                }
                else
                {
                    Errors.Add((_lookahead.Line, _lookahead.Column,
                        "Ожидался идентификатор после типа в инициализации for."));
                    init = new LiteralNode("void", string.Empty, -1, -1);
                }
            }
            else
            {
                init = new LiteralNode("void", string.Empty, -1, -1);
            }

            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' в инициализации for — обнаружена '{_lookahead.Lexeme}'"));
            }
            else
            {
                Consume();
            }
        }
        else
        {
            init = ParseCppExpr();
            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' в инициализации for — обнаружена '{_lookahead.Lexeme}'"));
            }
            else
            {
                Consume();
            }
        }

        // cond
        AstNode cond;
        if (Is(TokenType.Semicolon))
        {
            cond = new LiteralNode("void", string.Empty, -1, -1);
            Consume();
        }
        else
        {
            cond = ParseCppExpr();
            if (!Is(TokenType.Semicolon))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    $"Ожидалась ';' в условии for — обнаружена '{_lookahead.Lexeme}'"));
            }
            else
            {
                Consume();
            }
        }

        // incr
        AstNode incr = Is(TokenType.RParen)
            ? new LiteralNode("void", string.Empty, -1, -1)
            : ParseCppExpr();

        if (!Is(TokenType.RParen))
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                $"Ожидалась ')' в конце for — обнаружена '{_lookahead.Lexeme}'"));
        }
        else
        {
            Consume();
        }

        var body = ParseCppStatementOrBlock();
        _scopes.ExitScope();

        return new BinaryNode(
            "for",
            new BinaryNode("for-header", init, new BinaryNode("for-cond", cond, incr)),
            body);
    }

    private AstNode ParseCppBlock()
    {
        if (!Is(TokenType.LBrace))
            return new LiteralNode("error", string.Empty, _lookahead.Line, _lookahead.Column);

        Consume(); // {
        _scopes.EnterScope("block");

        var stmts = new List<AstNode>();
        while (_lookahead.Type != TokenType.RBrace &&
               _lookahead.Type != TokenType.EndOfFile)
        {
            if (Is(TokenType.Semicolon))
            {
                Consume();
                continue;
            }

            var stmt = ParseCppStatement();
            if (stmt != null)
                stmts.Add(stmt);
            else
                SkipToSemicolon();
        }

        if (!Is(TokenType.RBrace))
        {
            Errors.Add((_lookahead.Line, _lookahead.Column,
                $"Ожидалась '}}' для закрытия блока — обнаружена '{_lookahead.Lexeme}'"));
        }
        else
        {
            Consume();
        }

        _scopes.ExitScope();
        return new ProgramNode(stmts);
    }

    private AstNode ParseCppStatementOrBlock() =>
        Is(TokenType.LBrace)
            ? ParseCppBlock()
            : ParseCppStatement() ?? new LiteralNode("void", string.Empty, _lookahead.Line, _lookahead.Column);

    // ================= Выражения =================

    private AstNode ParseCppExpr() => ParseCppAssignment();

    private AstNode ParseCppAssignment()
    {
        var left = ParseCppLogicalOr();

        if (Is(TokenType.OpAssign))
        {
            var op = Consume();
            var right = ParseCppAssignment();

            if (left is IdentifierNode idLeft)
            {
                var leftEntry = _scopes.Lookup(idLeft.Name);
                if (leftEntry?.IsConst == true)
                {
                    Errors.Add((op.Line, op.Column,
                        $"Ошибка: нельзя присвоить значение константной переменной '{idLeft.Name}'"));
                }
                else if (leftEntry != null)
                {
                    string leftType = leftEntry.Type.ToLower();
                    string rightType = GetExpressionType(right);

                    if (!AreTypesCompatible(leftType, rightType))
                    {
                        Errors.Add((op.Line, op.Column,
                            $"Ошибка типов: несовместимые типы в присваивании {idLeft.Name} ({leftType}) = ... ({rightType})"));
                    }

                    leftEntry.IsInitialized = true;
                }
            }

            return new AssignNode(left, "=", right);
        }

        return left;
    }

    private AstNode ParseCppLogicalOr()
    {
        var left = ParseCppLogicalAnd();

        while (Is(TokenType.LogicalOr))
        {
            var op = Consume();
            var right = ParseCppLogicalAnd();
            left = new BinaryNode(op.Lexeme, left, right);
        }

        return left;
    }

    private AstNode ParseCppLogicalAnd()
    {
        var left = ParseCppEquality();

        while (Is(TokenType.LogicalAnd))
        {
            var op = Consume();
            var right = ParseCppEquality();
            left = new BinaryNode(op.Lexeme, left, right);
        }

        return left;
    }

    private AstNode ParseCppEquality()
    {
        var left = ParseCppRelational();

        while (Is(TokenType.OpEqual) || Is(TokenType.OpNotEqual))
        {
            var op = Consume();
            var right = ParseCppRelational();
            left = new BinaryNode(op.Lexeme, left, right);
        }

        return left;
    }

    private AstNode ParseCppRelational()
    {
        var left = ParseCppAdditive();

        while (Is(TokenType.OpLess) || Is(TokenType.OpGreater) ||
               Is(TokenType.OpLessEqual) || Is(TokenType.OpGreaterEqual) ||
               Is(TokenType.OpShiftLeft) || Is(TokenType.OpShiftRight))
        {
            var op = Consume();
            var right = ParseCppAdditive();
            left = new BinaryNode(op.Lexeme, left, right);
        }

        return left;
    }

    private AstNode ParseCppAdditive()
    {
        var left = ParseCppMultiplicative();

        while (Is(TokenType.OpPlus) || Is(TokenType.OpMinus))
        {
            var op = Consume();
            var right = ParseCppMultiplicative();
            left = new BinaryNode(op.Lexeme, left, right);
        }

        return left;
    }

    private AstNode ParseCppMultiplicative()
    {
        var left = ParseCppUnary();

        while (Is(TokenType.OpMultiply) || Is(TokenType.OpDivide) || Is(TokenType.OpModulo))
        {
            var op = Consume();
            var right = ParseCppUnary();
            left = new BinaryNode(op.Lexeme, left, right);
        }

        return left;
    }

    private AstNode ParseCppUnary()
    {
        if (Is(TokenType.OpPlus) || Is(TokenType.OpMinus) ||
            Is(TokenType.LogicalNot) || Is(TokenType.OpIncrement) ||
            Is(TokenType.OpDecrement))
        {
            var op = Consume();
            var operand = ParseCppUnary();
            return new UnaryNode(op.Lexeme, operand);
        }

        return ParseCppPostfix();
    }

    private AstNode ParseCppPostfix()
    {
        var expr = ParseCppPrimary();

        // Постфиксные ++/--
        while (Is(TokenType.OpIncrement) || Is(TokenType.OpDecrement))
        {
            var op = Consume();

            if (expr is IdentifierNode idExpr)
            {
                var entry = _scopes.Lookup(idExpr.Name);
                if (entry?.IsConst == true)
                {
                    Errors.Add((op.Line, op.Column,
                        $"Ошибка: нельзя инкрементировать/декрементировать константную переменную '{idExpr.Name}'"));
                }
                else if (entry != null)
                {
                    entry.IsInitialized = true;
                }
            }

            expr = new BinaryNode(op.Lexeme + "-post", expr,
                new LiteralNode("void", string.Empty, op.Line, op.Column));
        }

        // Индексация: []
        while (Is(TokenType.LBracket))
        {
            Consume();
            var index = ParseCppExpr();

            if (!Is(TokenType.RBracket))
            {
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    "Ожидалась ']' после индекса массива."));
            }
            else
            {
                Consume();
            }

            expr = new BinaryNode("[]", expr, index);
        }

        return expr;
    }

    private bool IsStdToken(TokenType type) =>
        type switch
        {
            TokenType.StdCout or TokenType.StdCin or TokenType.StdCerr or
            TokenType.StdClog or TokenType.StdEndl or TokenType.StdFlush or
            TokenType.StdWs or TokenType.StdMap or TokenType.StdSet or
            TokenType.StdList or TokenType.StdDeque or TokenType.StdQueue or
            TokenType.StdStack or TokenType.StdArray or TokenType.StdPair or
            TokenType.StdTuple or TokenType.StdOptional or TokenType.StdVariant or
            TokenType.StdIostream or TokenType.StdIomanip or
            TokenType.StdAlgorithm or TokenType.StdNumeric or
            TokenType.StdNamespace => true,
            _ => false
        };

    private AstNode ParseCppPrimary()
    {
        // new / new[]
        if (Is(TokenType.KeywordNew))
        {
            var newTok = Consume();
            if (!IsCppType(_lookahead.Type))
            {
                Errors.Add((newTok.Line, newTok.Column, "Ожидался тип после 'new'."));
                return new LiteralNode("error", "new", newTok.Line, newTok.Column);
            }

            var typeTok = Consume();
            AstNode? size = null;

            if (Is(TokenType.LBracket))
            {
                Consume();
                size = ParseCppExpr();

                if (!Is(TokenType.RBracket))
                    Errors.Add((_lookahead.Line, _lookahead.Column,
                        "Ожидалась ']' после размера массива в new."));
                else
                    Consume();
            }

            var typeNode = new IdentifierNode(typeTok.Lexeme, typeTok.Line, typeTok.Column);
            return size != null
                ? new BinaryNode("new-array", typeNode, size)
                : new UnaryNode("new", typeNode);
        }

        // (expr)
        if (Is(TokenType.LParen))
        {
            Consume();
            var expr = ParseCppExpr();

            if (!Is(TokenType.RParen))
                Errors.Add((_lookahead.Line, _lookahead.Column,
                    "Ожидалась закрывающая скобка ')' в выражении."));
            else
                Consume();

            return expr;
        }

        // Инициализатор {...}
        if (Is(TokenType.LBrace))
        {
            Consume();
            var elems = new List<AstNode>();

            while (!Is(TokenType.RBrace) && _lookahead.Type != TokenType.EndOfFile)
            {
                var elem = ParseCppExpr();
                elems.Add(elem);

                if (Is(TokenType.Comma))
                    Consume();
                else if (!Is(TokenType.RBrace))
                    break;
            }

            if (!Is(TokenType.RBrace))
                Errors.Add((_lookahead.Line, _lookahead.Column, "Ожидалась '}' в инициализаторе массива."));
            else
                Consume();

            return new ProgramNode(elems);
        }

        // std-идентификаторы без проверки объявления
        if (IsStdToken(_lookahead.Type))
        {
            var t = Consume();
            return new IdentifierNode(t.Lexeme, t.Line, t.Column);
        }

        // Обычные идентификаторы
        if (_lookahead.Type == TokenType.Identifier)
        {
            var t = Consume();
            _scopes.Require(t.Lexeme, t.Line, t.Column, Errors);
            return new IdentifierNode(t.Lexeme, t.Line, t.Column);
        }

        // Литералы
        if (_lookahead.Type == TokenType.Number)
        {
            var t = Consume();
            return new LiteralNode("number", t.Lexeme, t.Line, t.Column);
        }

        if (_lookahead.Type == TokenType.StringLiteral)
        {
            var t = Consume();
            return new LiteralNode("string", t.Lexeme, t.Line, t.Column);
        }

        if (_lookahead.Type == TokenType.CharLiteral)
        {
            var t = Consume();
            return new LiteralNode("char", t.Lexeme, t.Line, t.Column);
        }

        if (_lookahead.Type == TokenType.BoolTrue || _lookahead.Type == TokenType.BoolFalse)
        {
            var t = Consume();
            return new LiteralNode("bool", t.Lexeme, t.Line, t.Column);
        }

        // Ошибка
        Errors.Add((_lookahead.Line, _lookahead.Column,
            $"Ожидалось выражение, получено '{_lookahead.Lexeme}'."));
        var errTok = Consume();
        return new LiteralNode("error", errTok.Lexeme, errTok.Line, errTok.Column);
    }

    // ================= Типы выражений / совместимость =================

    private string GetExpressionType(AstNode? node)
    {
        if (node == null) return "unknown";

        return node switch
        {
            LiteralNode lit => GetLiteralType(lit),
            IdentifierNode id => GetIdentifierType(id),
            BinaryNode bin => GetBinaryType(bin),
            UnaryNode un => GetUnaryType(un),
            ProgramNode => "array",
            _ => "unknown"
        };
    }

    private string GetLiteralType(LiteralNode lit) =>
        lit.Kind switch
        {
            "number" => IsFloatLiteral(lit.Value) ? "double" : "int",
            "bool" => "bool",
            "string" => "string",
            "char" => "char",
            _ => "unknown"
        };

    private string GetIdentifierType(IdentifierNode id)
    {
        var entry = _scopes.Lookup(id.Name);
        return entry?.Type.ToLower() ?? "undeclared";
    }

    private string GetBinaryType(BinaryNode bin)
    {
        // Интрузивные случаи
        if (bin.Op == "[]")
            return GetExpressionType(bin.Left);

        if (bin.Op == "decl" && bin.Left is IdentifierNode typeId)
            return typeId.Name.ToLower();

        string leftType = GetExpressionType(bin.Left);
        string rightType = GetExpressionType(bin.Right);

        if (bin.Op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||")
            return "bool";

        if (IsArithmeticOp(bin.Op))
        {
            if (leftType == "double" || rightType == "double") return "double";
            if (leftType == "float" || rightType == "float") return "float";
            return "int";
        }

        return leftType;
    }

    private string GetUnaryType(UnaryNode un)
    {
        if (un.Op == "return")
            return GetExpressionType(un.Operand);

        if (un.Op == "!")
            return "bool";

        return GetExpressionType(un.Operand);
    }

    private static bool IsArithmeticOp(string op) =>
        op is "+" or "-" or "*" or "/" or "%";

    private static bool IsFloatLiteral(string value) =>
        value.Contains('.') ||
        value.Contains('e') || value.Contains('E') ||
        value.EndsWith("f", StringComparison.OrdinalIgnoreCase);

    private bool AreTypesCompatible(string targetType, string sourceType)
    {
        targetType = targetType.ToLower().Replace("*", "").Trim();
        sourceType = sourceType.ToLower().Replace("*", "").Trim();

        if (targetType == sourceType) return true;
        if (targetType == "void" || sourceType == "void") return true;
        if (sourceType == "undeclared" || sourceType == "unknown") return true;

        var numeric = new HashSet<string> { "int", "float", "double", "long", "short", "unsigned" };
        if (numeric.Contains(targetType) && numeric.Contains(sourceType)) return true;

        return false;
    }
}
