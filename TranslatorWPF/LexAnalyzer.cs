using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lexer;

/// <summary>
/// Перечисление всех типов токенов с конкретной привязкой к ключевым словам и операторам.
/// </summary>
public enum TokenType
{
    // Базовые типы токенов
    Identifier,     // Идентификаторы: переменные, функции
    Number,         // Числовые литералы
    StringLiteral,  // Строковые литералы
    CharLiteral,    // Символьные литералы

    // Булевы литералы
    BoolTrue,       // true
    BoolFalse,      // false

    // Типы данных
    TypeInt,        // int
    TypeFloat,      // float
    TypeDouble,     // double
    TypeChar,       // char
    TypeBool,       // bool
    TypeVoid,       // void
    TypeLong,       // long
    TypeShort,      // short
    TypeUnsigned,   // unsigned
    TypeSigned,     // signed
    TypeAuto,       // auto
    TypeString,     // string (std::string)
    TypeVector,     // vector (std::vector)

    // Модификаторы
    KeywordConst,   // const
    KeywordStatic,  // static
    KeywordVolatile,// volatile

    // Управление потоком
    KeywordIf,      // if
    KeywordElse,    // else
    KeywordSwitch,  // switch
    KeywordCase,    // case
    KeywordDefault, // default
    KeywordBreak,   // break
    KeywordContinue,// continue
    KeywordFor,     // for
    KeywordWhile,   // while
    KeywordDo,      // do
    KeywordReturn,  // return
    KeywordGoto,    // goto

    // Логические операторы (текстовые)
    LogicalAnd,     // and / &&
    LogicalOr,      // or / ||
    LogicalNot,     // not / !
    LogicalXor,     // xor / ^

    // ООП
    KeywordStruct,      // struct
    KeywordClass,       // class
    KeywordUnion,       // union
    KeywordEnum,        // enum
    KeywordNamespace,   // namespace
    KeywordUsing,       // using
    KeywordNew,         // new
    KeywordDelete,      // delete
    KeywordTemplate,    // template
    KeywordTypename,    // typename
    KeywordPublic,      // public
    KeywordPrivate,     // private
    KeywordProtected,   // protected

    // Встроенные std идентификаторы
    StdCout,        // cout
    StdCin,         // cin
    StdCerr,        // cerr
    StdClog,        // clog
    StdEndl,        // endl
    StdFlush,       // flush
    StdWs,          // ws
    StdMap,         // map
    StdSet,         // set
    StdList,        // list
    StdDeque,       // deque
    StdQueue,       // queue
    StdStack,       // stack
    StdArray,       // array
    StdPair,        // pair
    StdTuple,       // tuple
    StdOptional,    // optional
    StdVariant,     // variant
    StdIostream,    // iostream
    StdIomanip,     // iomanip
    StdAlgorithm,   // algorithm
    StdNumeric,     // numeric
    StdNamespace,   // std

    // Операторы сравнения
    OpEqual,        // ==
    OpNotEqual,     // !=
    OpLessEqual,    // <=
    OpGreaterEqual, // >=
    OpLess,         // <
    OpGreater,      // >

    // Битовые сдвиги и потоковые операторы
    OpShiftLeft,    // <<
    OpShiftRight,   // >>

    // Арифметические операторы
    OpPlus,         // +
    OpMinus,        // -
    OpMultiply,     // *
    OpDivide,       // /
    OpModulo,       // %

    // Унарные операторы
    OpIncrement,    // ++
    OpDecrement,    // --

    // Битовые операторы
    OpBitAnd,       // & (битовое И)
    OpBitOr,        // | (битовое ИЛИ)
    OpBitXor,       // ^ (битовое XOR, если не xor)
    OpBitNot,       // ~ (битовое отрицание)

    // Присваивание
    OpAssign,       // =

    // Прочие операторы
    OpTernary,      // ?

    // Разделители
    LParen,         // (
    RParen,         // )
    LBrace,         // {
    RBrace,         // }
    LBracket,       // [
    RBracket,       // ]
    Semicolon,      // ;
    Comma,          // ,
    Dot,            // .
    Colon,          // :
    Arrow,          // ->
    DoubleColon,    // ::

    // Служебные типы
    Preprocessor,   // Директивы препроцессора
    Comment,        // Комментарии
    Whitespace,     // Пробелы (в текущей реализации не выдаётся как токен)
    EndOfFile,      // Конец файла
    Unknown         // Неизвестный токен
}

/// <summary>
/// Структура токена.
/// </summary>
public readonly record struct Token(TokenType Type, string Lexeme, int Line, int Column);

/// <summary>
/// Класс грамматики языка: здесь хранятся все соответствия лексем токенам.
/// </summary>
public class LanguageGrammar
{
    // Общие настройки лексем
    public string LineCommentStart { get; set; } = "//";
    public string BlockCommentStart { get; set; } = "/*";
    public string BlockCommentEnd { get; set; } = "*/";
    public char PreprocessorChar { get; set; } = '#';
    public char StringDelimiter { get; set; } = '"';
    public char CharDelimiter { get; set; } = '\'';
    public char EscapeChar { get; set; } = '\\';

    /// <summary>
    /// Ключевые слова и специальные идентификаторы.
    /// </summary>
    public Dictionary<string, TokenType> Keywords { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Двухсимвольные операторы (==, !=, &&, ||, ...).
    /// </summary>
    public Dictionary<string, TokenType> TwoCharOperators { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Односимвольные операторы и разделители.
    /// </summary>
    public Dictionary<char, TokenType> SingleCharTokens { get; set; } =
        new();
}

/// <summary>
/// Лексический анализатор с конкретными токенами, использующий грамматику.
/// </summary>
public sealed class LexAnalyzer
{
    private readonly string _input;
    private readonly LanguageGrammar _grammar;
    private int _pos = 0;
    private int _line = 1;
    private int _column = 1;
    private const char EOF_CHAR = '\0';

    public List<(int Line, int Col, string Message)> Errors { get; } = new();

    public LexAnalyzer(string input, LanguageGrammar grammar)
    {
        _input = input ?? string.Empty;
        _grammar = grammar ?? throw new ArgumentNullException(nameof(grammar));
    }

    public List<Token> Scan()
    {
        var tokens = new List<Token>();

        while (_pos < _input.Length)
        {
            var token = ScanToken();
            if (token.Type != TokenType.Unknown &&
                token.Type != TokenType.Whitespace &&
                token.Type != TokenType.Comment)
            {
                tokens.Add(token);
            }
        }

        tokens.Add(new Token(TokenType.EndOfFile, string.Empty, _line, _column));
        return tokens;
    }

    private Token ScanToken()
    {
        SkipWhitespace();

        if (_pos >= _input.Length)
            return new Token(TokenType.EndOfFile, string.Empty, _line, _column);

        char ch = _input[_pos];

        // Комментарии (однострочные)
        if (ch == '/' && PeekNext() == '/')
        {
            SkipLineComment();
            return new Token(TokenType.Comment, _grammar.LineCommentStart, _line, _column);
        }

        // Комментарии (блочные)
        if (ch == '/' && PeekNext() == '*')
        {
            SkipBlockComment();
            return new Token(TokenType.Comment, _grammar.BlockCommentStart + _grammar.BlockCommentEnd, _line, _column);
        }

        // Препроцессор
        if (ch == _grammar.PreprocessorChar)
            return ScanPreprocessor();

        // Числа
        if (char.IsDigit(ch))
            return ScanNumber();

        // Строки
        if (ch == _grammar.StringDelimiter)
            return ScanStringLiteral();

        // Символы
        if (ch == _grammar.CharDelimiter)
            return ScanCharLiteral();

        // Идентификаторы и ключевые слова
        if (char.IsLetter(ch) || ch == '_')
            return ScanIdentifierOrKeyword();

        // Операторы и разделители
        return ScanOperatorOrDelimiter();
    }

    private void SkipWhitespace()
    {
        while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
        {
            if (_input[_pos] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _pos++;
        }
    }

    private char Current => _pos < _input.Length ? _input[_pos] : EOF_CHAR;
    private char PeekNext() => _pos + 1 < _input.Length ? _input[_pos + 1] : EOF_CHAR;

    private void Advance()
    {
        if (_pos < _input.Length)
        {
            if (_input[_pos] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _pos++;
        }
    }

    private void SkipLineComment()
    {
        while (_pos < _input.Length && _input[_pos] != '\n')
            Advance();
    }

    private void SkipBlockComment()
    {
        int startLine = _line;
        int startCol = _column;

        Advance(); // '/'
        Advance(); // '*'

        bool closed = false;

        while (_pos < _input.Length)
        {
            if (Current == '*' && PeekNext() == '/')
            {
                Advance(); // '*'
                Advance(); // '/'
                closed = true;
                break;
            }

            Advance();
        }

        if (!closed)
        {
            Errors.Add((startLine, startCol, "Незакрытый блочный комментарий"));
        }
    }

    private Token ScanPreprocessor()
    {
        int startLine = _line;
        int startCol = _column;
        var sb = new StringBuilder();

        while (_pos < _input.Length && _input[_pos] != '\n')
        {
            sb.Append(_input[_pos]);
            Advance();
        }

        return new Token(TokenType.Preprocessor, sb.ToString(), startLine, startCol);
    }

    private Token ScanNumber()
    {
        int startLine = _line;
        int startCol = _column;
        var sb = new StringBuilder();

        // Hex 0x...
        if (Current == '0' && (PeekNext() == 'x' || PeekNext() == 'X'))
        {
            sb.Append(Current);
            Advance();
            sb.Append(Current);
            Advance();

            int hexStartPos = _pos;
            while (_pos < _input.Length && IsHexDigit(Current))
            {
                sb.Append(Current);
                Advance();
            }

            if (_pos == hexStartPos)
            {
                // Нет ни одной шестнадцатеричной цифры после 0x
                Errors.Add((_line, _column, "Ожидалась шестнадцатеричная цифра после префикса 0x"));
            }

            return new Token(TokenType.Number, sb.ToString(), startLine, startCol);
        }

        // Binary 0b...
        if (Current == '0' && (PeekNext() == 'b' || PeekNext() == 'B'))
        {
            sb.Append(Current);
            Advance();
            sb.Append(Current);
            Advance();

            int binStartPos = _pos;
            while (_pos < _input.Length && (Current == '0' || Current == '1'))
            {
                sb.Append(Current);
                Advance();
            }

            if (_pos == binStartPos)
            {
                // Нет ни одной двоичной цифры после 0b
                Errors.Add((_line, _column, "Ожидалась двоичная цифра после префикса 0b"));
            }

            return new Token(TokenType.Number, sb.ToString(), startLine, startCol);
        }

        // Decimal / floating
        bool hasDot = false;

        // Целая часть
        while (_pos < _input.Length && char.IsDigit(Current))
        {
            sb.Append(Current);
            Advance();
        }

        // Возможная дробная часть
        while (_pos < _input.Length && (Current == '.' || char.IsDigit(Current)))
        {
            if (Current == '.')
            {
                if (hasDot)
                {
                    // Вторая точка в числе с плавающей точкой: 13.3.3
                    Errors.Add((_line, _column, "Слишком много точек в числе с плавающей точкой"));
                    // Не поглощаем вторую точку, чтобы она пошла как отдельный токен '.'
                    break;
                }

                // Если после точки нет цифры, считаем, что это просто целое число и точка отдельно
                if (!char.IsDigit(PeekNext()))
                    break;

                hasDot = true;
                sb.Append(Current);
                Advance();
            }
            else
            {
                // Цифры после точки или продолжение целой части
                sb.Append(Current);
                Advance();
            }
        }

        // Сюда можно дописать более строгие проверки суффиксов, если нужно
        while (_pos < _input.Length && (char.IsLetter(Current) || Current == '_'))
        {
            sb.Append(Current);
            Advance();
        }

        return new Token(TokenType.Number, sb.ToString(), startLine, startCol);
    }

    private static bool IsHexDigit(char ch) =>
        char.IsDigit(ch) || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

    private Token ScanStringLiteral()
    {
        int startLine = _line;
        int startCol = _column;
        var sb = new StringBuilder();

        sb.Append(Current); // Открывающая кавычка
        Advance();

        while (_pos < _input.Length && Current != _grammar.StringDelimiter)
        {
            if (Current == _grammar.EscapeChar)
            {
                sb.Append(Current);
                Advance();

                if (_pos < _input.Length)
                {
                    sb.Append(Current);
                    Advance();
                }
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (Current == _grammar.StringDelimiter)
        {
            sb.Append(Current);
            Advance();
        }
        else
        {
            Errors.Add((_line, _column, "Незакрытая строка"));
        }

        return new Token(TokenType.StringLiteral, sb.ToString(), startLine, startCol);
    }

    private Token ScanCharLiteral()
    {
        int startLine = _line;
        int startCol = _column;
        var sb = new StringBuilder();

        sb.Append(Current); // Открывающая кавычка
        Advance();

        if (Current == _grammar.EscapeChar)
        {
            sb.Append(Current);
            Advance();

            if (_pos < _input.Length)
            {
                sb.Append(Current);
                Advance();
            }
        }
        else if (Current != _grammar.CharDelimiter && Current != EOF_CHAR)
        {
            sb.Append(Current);
            Advance();
        }

        if (Current == _grammar.CharDelimiter)
        {
            sb.Append(Current);
            Advance();
        }
        else
        {
            Errors.Add((_line, _column, "Незакрытый символьный литерал"));
        }

        return new Token(TokenType.CharLiteral, sb.ToString(), startLine, startCol);
    }

    private Token ScanIdentifierOrKeyword()
    {
        int startLine = _line;
        int startCol = _column;
        var sb = new StringBuilder();

        while (_pos < _input.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
        {
            sb.Append(Current);
            Advance();
        }

        string lexeme = sb.ToString();

        if (_grammar.Keywords.TryGetValue(lexeme, out var tokenType))
            return new Token(tokenType, lexeme, startLine, startCol);

        return new Token(TokenType.Identifier, lexeme, startLine, startCol);
    }

    private Token ScanOperatorOrDelimiter()
    {
        int startLine = _line;
        int startCol = _column;
        char ch = Current;

        // Двухсимвольные операторы
        if (_pos + 1 < _input.Length)
        {
            string twoChar = new(new[] { ch, PeekNext() });
            if (_grammar.TwoCharOperators.TryGetValue(twoChar, out var type))
            {
                Advance();
                Advance();
                return new Token(type, twoChar, startLine, startCol);
            }
        }

        // Односимвольные операторы и разделители
        Advance();
        if (_grammar.SingleCharTokens.TryGetValue(ch, out var singleType))
            return new Token(singleType, ch.ToString(), startLine, startCol);

        return new Token(TokenType.Unknown, ch.ToString(), startLine, startCol);
    }
}

/// <summary>
/// Фабрика грамматик языков.
/// </summary>
public static class LanguageGrammarFactory
{
    public static LanguageGrammar CreateCppGrammar()
    {
        return new LanguageGrammar
        {
            LineCommentStart = "//",
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            PreprocessorChar = '#',
            StringDelimiter = '"',
            CharDelimiter = '\'',
            EscapeChar = '\\',

            Keywords = new Dictionary<string, TokenType>(StringComparer.Ordinal)
            {
                // Типы данных
                ["int"] = TokenType.TypeInt,
                ["float"] = TokenType.TypeFloat,
                ["double"] = TokenType.TypeDouble,
                ["char"] = TokenType.TypeChar,
                ["bool"] = TokenType.TypeBool,
                ["void"] = TokenType.TypeVoid,
                ["long"] = TokenType.TypeLong,
                ["short"] = TokenType.TypeShort,
                ["unsigned"] = TokenType.TypeUnsigned,
                ["signed"] = TokenType.TypeSigned,
                ["auto"] = TokenType.TypeAuto,
                ["string"] = TokenType.TypeString,
                ["vector"] = TokenType.TypeVector,

                // Модификаторы
                ["const"] = TokenType.KeywordConst,
                ["static"] = TokenType.KeywordStatic,
                ["volatile"] = TokenType.KeywordVolatile,

                // Управление потоком
                ["if"] = TokenType.KeywordIf,
                ["else"] = TokenType.KeywordElse,
                ["switch"] = TokenType.KeywordSwitch,
                ["case"] = TokenType.KeywordCase,
                ["default"] = TokenType.KeywordDefault,
                ["break"] = TokenType.KeywordBreak,
                ["continue"] = TokenType.KeywordContinue,
                ["for"] = TokenType.KeywordFor,
                ["while"] = TokenType.KeywordWhile,
                ["do"] = TokenType.KeywordDo,
                ["return"] = TokenType.KeywordReturn,
                ["goto"] = TokenType.KeywordGoto,

                // Логические операторы (текстовые варианты)
                ["and"] = TokenType.LogicalAnd,
                ["or"] = TokenType.LogicalOr,
                ["not"] = TokenType.LogicalNot,
                ["xor"] = TokenType.LogicalXor,

                // ООП
                ["struct"] = TokenType.KeywordStruct,
                ["class"] = TokenType.KeywordClass,
                ["union"] = TokenType.KeywordUnion,
                ["enum"] = TokenType.KeywordEnum,
                ["namespace"] = TokenType.KeywordNamespace,
                ["using"] = TokenType.KeywordUsing,
                ["new"] = TokenType.KeywordNew,
                ["delete"] = TokenType.KeywordDelete,
                ["template"] = TokenType.KeywordTemplate,
                ["typename"] = TokenType.KeywordTypename,
                ["public"] = TokenType.KeywordPublic,
                ["private"] = TokenType.KeywordPrivate,
                ["protected"] = TokenType.KeywordProtected,

                // Булевы литералы
                ["true"] = TokenType.BoolTrue,
                ["false"] = TokenType.BoolFalse,

                // Встроенные std
                ["cout"] = TokenType.StdCout,
                ["cin"] = TokenType.StdCin,
                ["cerr"] = TokenType.StdCerr,
                ["clog"] = TokenType.StdClog,
                ["endl"] = TokenType.StdEndl,
                ["flush"] = TokenType.StdFlush,
                ["ws"] = TokenType.StdWs,
                ["map"] = TokenType.StdMap,
                ["set"] = TokenType.StdSet,
                ["list"] = TokenType.StdList,
                ["deque"] = TokenType.StdDeque,
                ["queue"] = TokenType.StdQueue,
                ["stack"] = TokenType.StdStack,
                ["array"] = TokenType.StdArray,
                ["pair"] = TokenType.StdPair,
                ["tuple"] = TokenType.StdTuple,
                ["optional"] = TokenType.StdOptional,
                ["variant"] = TokenType.StdVariant,
                ["iostream"] = TokenType.StdIostream,
                ["iomanip"] = TokenType.StdIomanip,
                ["algorithm"] = TokenType.StdAlgorithm,
                ["numeric"] = TokenType.StdNumeric,
                ["std"] = TokenType.StdNamespace,
            },

            TwoCharOperators = new Dictionary<string, TokenType>(StringComparer.Ordinal)
            {
                ["=="] = TokenType.OpEqual,
                ["!="] = TokenType.OpNotEqual,
                ["<="] = TokenType.OpLessEqual,
                [">="] = TokenType.OpGreaterEqual,
                ["<<"] = TokenType.OpShiftLeft,
                [">>"] = TokenType.OpShiftRight,
                ["&&"] = TokenType.LogicalAnd,
                ["||"] = TokenType.LogicalOr,
                ["++"] = TokenType.OpIncrement,
                ["--"] = TokenType.OpDecrement,
                ["->"] = TokenType.Arrow,
                ["::"] = TokenType.DoubleColon,
            },

            SingleCharTokens = new Dictionary<char, TokenType>
            {
                ['('] = TokenType.LParen,
                [')'] = TokenType.RParen,
                ['{'] = TokenType.LBrace,
                ['}'] = TokenType.RBrace,
                ['['] = TokenType.LBracket,
                [']'] = TokenType.RBracket,
                [';'] = TokenType.Semicolon,
                [','] = TokenType.Comma,
                ['.'] = TokenType.Dot,
                [':'] = TokenType.Colon,
                ['+'] = TokenType.OpPlus,
                ['-'] = TokenType.OpMinus,
                ['*'] = TokenType.OpMultiply,
                ['/'] = TokenType.OpDivide,
                ['%'] = TokenType.OpModulo,
                ['='] = TokenType.OpAssign,
                ['<'] = TokenType.OpLess,
                ['>'] = TokenType.OpGreater,
                ['!'] = TokenType.LogicalNot,
                ['&'] = TokenType.OpBitAnd,
                ['|'] = TokenType.OpBitOr,
                ['^'] = TokenType.OpBitXor,
                ['~'] = TokenType.OpBitNot,
                ['?'] = TokenType.OpTernary,
            }
        };
    }
}
