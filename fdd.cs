using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace FddCompiler;

internal static class Program
{
    private static readonly MethodInfo WriteLineMethod =
        typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(int) })!;

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        string source;
        try
        {
            if (args.Length == 2 && args[0] == "-e")
            {
                source = args[1];
            }
            else if (args.Length == 1)
            {
                source = File.ReadAllText(args[0]);
            }
            else
            {
                PrintUsage();
                return 2;
            }

            RunSource(source);
            return 0;
        }
        catch (CompileException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Не удалось прочитать файл: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Нет доступа к файлу: {exception.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FDD — простой компилятор целочисленного языка");
        Console.WriteLine("Использование: fdd <файл> | fdd -e \"исходный код\"");
        Console.WriteLine("Ключи: -h, --help");
    }

    internal static void RunSource(string source)
    {
        var statements = new Parser(source).ParseProgram();
        new IlCompiler().Compile(statements)();
    }

    private sealed class IlCompiler
    {
        private readonly ILGenerator _il;
        private readonly Stack<Dictionary<string, LocalBuilder>> _scopes = new();

        public IlCompiler()
        {
            var method = new DynamicMethod(
                "FddProgram",
                typeof(void),
                Type.EmptyTypes,
                typeof(Program).Module);
            _il = method.GetILGenerator();
            _scopes.Push(new Dictionary<string, LocalBuilder>(StringComparer.Ordinal));
            _method = method;
        }

        private readonly DynamicMethod _method;

        public Action Compile(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
            {
                EmitStatement(statement);
            }

            _il.Emit(OpCodes.Ret);
            return (Action)_method.CreateDelegate(typeof(Action));
        }

        private void EmitStatement(Statement statement)
        {
            switch (statement)
            {
                case LetStatement let:
                    EmitLet(let);
                    break;
                case AssignStatement assignment:
                    EmitExpression(assignment.Value);
                    _il.Emit(OpCodes.Stloc, FindVariable(assignment.Name, assignment.Line));
                    break;
                case PrintStatement print:
                    EmitExpression(print.Value);
                    _il.Emit(OpCodes.Call, WriteLineMethod);
                    break;
                case IfStatement conditional:
                    EmitIf(conditional);
                    break;
                case WhileStatement loop:
                    EmitWhile(loop);
                    break;
                case BlockStatement block:
                    EmitBlock(block);
                    break;
                default:
                    throw new InvalidOperationException($"Неизвестный тип оператора: {statement.GetType().Name}");
            }
        }

        private void EmitLet(LetStatement statement)
        {
            var scope = _scopes.Peek();
            if (scope.ContainsKey(statement.Name))
            {
                throw new CompileException(statement.Line, $"Переменная '{statement.Name}' уже объявлена в этой области.");
            }

            EmitExpression(statement.Value);
            var local = _il.DeclareLocal(typeof(int));
            _il.Emit(OpCodes.Stloc, local);
            scope.Add(statement.Name, local);
        }

        private void EmitIf(IfStatement statement)
        {
            var elseLabel = _il.DefineLabel();
            var endLabel = _il.DefineLabel();

            EmitExpression(statement.Condition);
            _il.Emit(OpCodes.Brfalse, elseLabel);
            EmitScopedStatement(statement.Then);

            if (statement.Else is not null)
            {
                _il.Emit(OpCodes.Br, endLabel);
                _il.MarkLabel(elseLabel);
                EmitScopedStatement(statement.Else);
                _il.MarkLabel(endLabel);
            }
            else
            {
                _il.MarkLabel(elseLabel);
            }
        }

        private void EmitWhile(WhileStatement statement)
        {
            var startLabel = _il.DefineLabel();
            var endLabel = _il.DefineLabel();

            _il.MarkLabel(startLabel);
            EmitExpression(statement.Condition);
            _il.Emit(OpCodes.Brfalse, endLabel);
            EmitScopedStatement(statement.Body);
            _il.Emit(OpCodes.Br, startLabel);
            _il.MarkLabel(endLabel);
        }

        private void EmitScopedStatement(Statement statement)
        {
            _scopes.Push(new Dictionary<string, LocalBuilder>(StringComparer.Ordinal));
            EmitStatement(statement);
            _scopes.Pop();
        }

        private void EmitBlock(BlockStatement block)
        {
            _scopes.Push(new Dictionary<string, LocalBuilder>(StringComparer.Ordinal));
            foreach (var statement in block.Statements)
            {
                EmitStatement(statement);
            }
            _scopes.Pop();
        }

        private void EmitExpression(Expression expression)
        {
            switch (expression)
            {
                case IntegerExpression integer:
                    _il.Emit(OpCodes.Ldc_I4, integer.Value);
                    break;
                case VariableExpression variable:
                    _il.Emit(OpCodes.Ldloc, FindVariable(variable.Name, variable.Line));
                    break;
                case UnaryExpression unary:
                    EmitUnary(unary);
                    break;
                case BinaryExpression binary:
                    EmitBinary(binary);
                    break;
                default:
                    throw new InvalidOperationException($"Неизвестный тип выражения: {expression.GetType().Name}");
            }
        }

        private void EmitUnary(UnaryExpression expression)
        {
            EmitExpression(expression.Operand);
            switch (expression.Operator)
            {
                case "-":
                    _il.Emit(OpCodes.Neg);
                    break;
                case "!":
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ceq);
                    break;
                case "+":
                    break;
                default:
                    throw new InvalidOperationException($"Неизвестный унарный оператор: {expression.Operator}");
            }
        }

        private void EmitBinary(BinaryExpression expression)
        {
            if (expression.Operator is "&&" or "||")
            {
                EmitLogical(expression);
                return;
            }

            EmitExpression(expression.Left);
            EmitExpression(expression.Right);
            switch (expression.Operator)
            {
                case "+": _il.Emit(OpCodes.Add); break;
                case "-": _il.Emit(OpCodes.Sub); break;
                case "*": _il.Emit(OpCodes.Mul); break;
                case "/": _il.Emit(OpCodes.Div); break;
                case "%": _il.Emit(OpCodes.Rem); break;
                case "==": _il.Emit(OpCodes.Ceq); break;
                case "!=":
                    _il.Emit(OpCodes.Ceq);
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ceq);
                    break;
                case "<": _il.Emit(OpCodes.Clt); break;
                case ">": _il.Emit(OpCodes.Cgt); break;
                case "<=":
                    _il.Emit(OpCodes.Cgt);
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ceq);
                    break;
                case ">=":
                    _il.Emit(OpCodes.Clt);
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ceq);
                    break;
                default:
                    throw new InvalidOperationException($"Неизвестный бинарный оператор: {expression.Operator}");
            }
        }

        private void EmitLogical(BinaryExpression expression)
        {
            var shortCircuit = _il.DefineLabel();
            var end = _il.DefineLabel();

            EmitExpression(expression.Left);
            _il.Emit(expression.Operator == "&&" ? OpCodes.Brfalse : OpCodes.Brtrue, shortCircuit);
            EmitExpression(expression.Right);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Cgt_Un);
            _il.Emit(OpCodes.Br, end);

            _il.MarkLabel(shortCircuit);
            _il.Emit(expression.Operator == "&&" ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1);
            _il.MarkLabel(end);
        }

        private LocalBuilder FindVariable(string name, int line)
        {
            foreach (var scope in _scopes)
            {
                if (scope.TryGetValue(name, out var local))
                {
                    return local;
                }
            }

            throw new CompileException(line, $"Переменная '{name}' не объявлена.");
        }
    }
}

public static class FddEngine
{
    public static void Execute(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Program.RunSource(source);
    }
}

internal abstract record Statement(int Line);
internal sealed record LetStatement(string Name, Expression Value, int Line) : Statement(Line);
internal sealed record AssignStatement(string Name, Expression Value, int Line) : Statement(Line);
internal sealed record PrintStatement(Expression Value, int Line) : Statement(Line);
internal sealed record IfStatement(Expression Condition, Statement Then, Statement? Else, int Line) : Statement(Line);
internal sealed record WhileStatement(Expression Condition, Statement Body, int Line) : Statement(Line);
internal sealed record BlockStatement(IReadOnlyList<Statement> Statements, int Line) : Statement(Line);

internal abstract record Expression(int Line);
internal sealed record IntegerExpression(int Value, int Line) : Expression(Line);
internal sealed record VariableExpression(string Name, int Line) : Expression(Line);
internal sealed record UnaryExpression(string Operator, Expression Operand, int Line) : Expression(Line);
internal sealed record BinaryExpression(string Operator, Expression Left, Expression Right, int Line) : Expression(Line);

public sealed class CompileException : Exception
{
    public CompileException(int line, string message)
        : base($"Строка {line}: {message}")
    {
    }
}

internal enum TokenKind
{
    End, Number, Identifier,
    Let, Print, If, Else, While, True, False,
    LeftParen, RightParen, LeftBrace, RightBrace, Semicolon,
    Plus, Minus, Star, Slash, Percent, Bang,
    Equal, EqualEqual, BangEqual, Less, LessEqual, Greater, GreaterEqual,
    AndAnd, OrOr
}

internal sealed record Token(TokenKind Kind, string Text, int Line);

internal sealed class Lexer
{
    private readonly string _source;
    private readonly List<Token> _tokens = new();
    private int _position;
    private int _line = 1;

    public Lexer(string source) => _source = source;

    public IReadOnlyList<Token> Scan()
    {
        while (!AtEnd)
        {
            var current = Peek();
            if (char.IsWhiteSpace(current))
            {
                if (current == '\n') _line++;
                _position++;
                continue;
            }

            if (current == '/' && PeekNext() == '/')
            {
                while (!AtEnd && Peek() != '\n') _position++;
                continue;
            }

            var tokenLine = _line;
            if (char.IsDigit(current))
            {
                ScanNumber(tokenLine);
            }
            else if (char.IsLetter(current) || current == '_')
            {
                ScanIdentifier(tokenLine);
            }
            else
            {
                ScanSymbol(tokenLine);
            }
        }

        _tokens.Add(new Token(TokenKind.End, string.Empty, _line));
        return _tokens;
    }

    private bool AtEnd => _position >= _source.Length;
    private char Peek() => AtEnd ? '\0' : _source[_position];
    private char PeekNext() => _position + 1 >= _source.Length ? '\0' : _source[_position + 1];

    private void ScanNumber(int line)
    {
        var start = _position;
        while (char.IsDigit(Peek())) _position++;
        var text = _source[start.._position];
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value > (long)int.MaxValue + 1)
        {
            throw new CompileException(line, $"Целое число вне диапазона Int32: '{text}'.");
        }
        _tokens.Add(new Token(TokenKind.Number, text, line));
    }

    private void ScanIdentifier(int line)
    {
        var start = _position++;
        while (char.IsLetterOrDigit(Peek()) || Peek() == '_') _position++;
        var text = _source[start.._position];
        var kind = text switch
        {
            "let" => TokenKind.Let,
            "print" => TokenKind.Print,
            "if" => TokenKind.If,
            "else" => TokenKind.Else,
            "while" => TokenKind.While,
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            _ => TokenKind.Identifier
        };
        _tokens.Add(new Token(kind, text, line));
    }

    private void ScanSymbol(int line)
    {
        var start = _position;
        var character = _source[_position++];
        var kind = character switch
        {
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            '{' => TokenKind.LeftBrace,
            '}' => TokenKind.RightBrace,
            ';' => TokenKind.Semicolon,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '%' => TokenKind.Percent,
            '!' => Match('=') ? TokenKind.BangEqual : TokenKind.Bang,
            '=' => Match('=') ? TokenKind.EqualEqual : TokenKind.Equal,
            '<' => Match('=') ? TokenKind.LessEqual : TokenKind.Less,
            '>' => Match('=') ? TokenKind.GreaterEqual : TokenKind.Greater,
            '&' when Match('&') => TokenKind.AndAnd,
            '|' when Match('|') => TokenKind.OrOr,
            _ => throw new CompileException(line, $"Неожиданный символ '{character}'.")
        };
        _tokens.Add(new Token(kind, _source[start.._position], line));
    }

    private bool Match(char expected)
    {
        if (AtEnd || Peek() != expected) return false;
        _position++;
        return true;
    }
}

internal sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _position;

    public Parser(string source) => _tokens = new Lexer(source).Scan();

    public IReadOnlyList<Statement> ParseProgram()
    {
        var statements = new List<Statement>();
        while (!Check(TokenKind.End))
        {
            statements.Add(ParseStatement());
        }
        return statements;
    }

    private Statement ParseStatement()
    {
        if (Match(TokenKind.Let)) return ParseLet(Previous);
        if (Match(TokenKind.Print)) return ParsePrint(Previous);
        if (Match(TokenKind.If)) return ParseIf(Previous);
        if (Match(TokenKind.While)) return ParseWhile(Previous);
        if (Match(TokenKind.LeftBrace)) return ParseBlock(Previous);
        if (Check(TokenKind.Identifier) && CheckNext(TokenKind.Equal))
        {
            var name = Advance();
            Advance();
            var value = ParseExpression();
            Consume(TokenKind.Semicolon, "Ожидалась ';' после присваивания.");
            return new AssignStatement(name.Text, value, name.Line);
        }

        throw Error(Current, "Ожидался оператор: let, print, if, while, блок или присваивание.");
    }

    private Statement ParseLet(Token keyword)
    {
        var name = Consume(TokenKind.Identifier, "Ожидалось имя переменной после 'let'.");
        Consume(TokenKind.Equal, "Ожидался '=' после имени переменной.");
        var value = ParseExpression();
        Consume(TokenKind.Semicolon, "Ожидалась ';' после объявления переменной.");
        return new LetStatement(name.Text, value, keyword.Line);
    }

    private Statement ParsePrint(Token keyword)
    {
        var value = ParseExpression();
        Consume(TokenKind.Semicolon, "Ожидалась ';' после выражения print.");
        return new PrintStatement(value, keyword.Line);
    }

    private Statement ParseIf(Token keyword)
    {
        Consume(TokenKind.LeftParen, "Ожидалась '(' после 'if'.");
        var condition = ParseExpression();
        Consume(TokenKind.RightParen, "Ожидалась ')' после условия.");
        var then = ParseStatement();
        var otherwise = Match(TokenKind.Else) ? ParseStatement() : null;
        return new IfStatement(condition, then, otherwise, keyword.Line);
    }

    private Statement ParseWhile(Token keyword)
    {
        Consume(TokenKind.LeftParen, "Ожидалась '(' после 'while'.");
        var condition = ParseExpression();
        Consume(TokenKind.RightParen, "Ожидалась ')' после условия.");
        return new WhileStatement(condition, ParseStatement(), keyword.Line);
    }

    private Statement ParseBlock(Token openingBrace)
    {
        var statements = new List<Statement>();
        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.End))
        {
            statements.Add(ParseStatement());
        }
        Consume(TokenKind.RightBrace, "Ожидалась '}' после блока.");
        return new BlockStatement(statements, openingBrace.Line);
    }

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr()
    {
        var expression = ParseAnd();
        while (Match(TokenKind.OrOr))
        {
            var op = Previous;
            expression = new BinaryExpression(op.Text, expression, ParseAnd(), op.Line);
        }
        return expression;
    }

    private Expression ParseAnd()
    {
        var expression = ParseEquality();
        while (Match(TokenKind.AndAnd))
        {
            var op = Previous;
            expression = new BinaryExpression(op.Text, expression, ParseEquality(), op.Line);
        }
        return expression;
    }

    private Expression ParseEquality()
    {
        var expression = ParseComparison();
        while (Match(TokenKind.EqualEqual, TokenKind.BangEqual))
        {
            var op = Previous;
            expression = new BinaryExpression(op.Text, expression, ParseComparison(), op.Line);
        }
        return expression;
    }

    private Expression ParseComparison()
    {
        var expression = ParseTerm();
        while (Match(TokenKind.Less, TokenKind.LessEqual, TokenKind.Greater, TokenKind.GreaterEqual))
        {
            var op = Previous;
            expression = new BinaryExpression(op.Text, expression, ParseTerm(), op.Line);
        }
        return expression;
    }

    private Expression ParseTerm()
    {
        var expression = ParseFactor();
        while (Match(TokenKind.Plus, TokenKind.Minus))
        {
            var op = Previous;
            expression = new BinaryExpression(op.Text, expression, ParseFactor(), op.Line);
        }
        return expression;
    }

    private Expression ParseFactor()
    {
        var expression = ParseUnary();
        while (Match(TokenKind.Star, TokenKind.Slash, TokenKind.Percent))
        {
            var op = Previous;
            expression = new BinaryExpression(op.Text, expression, ParseUnary(), op.Line);
        }
        return expression;
    }

    private Expression ParseUnary()
    {
        if (Match(TokenKind.Bang, TokenKind.Minus, TokenKind.Plus))
        {
            var op = Previous;
            if (op.Kind == TokenKind.Minus
                && Check(TokenKind.Number)
                && Current.Text == ((long)int.MaxValue + 1).ToString(CultureInfo.InvariantCulture))
            {
                Advance();
                return new IntegerExpression(int.MinValue, op.Line);
            }
            return new UnaryExpression(op.Text, ParseUnary(), op.Line);
        }
        return ParsePrimary();
    }

    private Expression ParsePrimary()
    {
        if (Match(TokenKind.Number))
        {
            var token = Previous;
            if (!int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw Error(token, $"Целое число вне диапазона Int32: '{token.Text}'.");
            }
            return new IntegerExpression(value, token.Line);
        }
        if (Match(TokenKind.True)) return new IntegerExpression(1, Previous.Line);
        if (Match(TokenKind.False)) return new IntegerExpression(0, Previous.Line);
        if (Match(TokenKind.Identifier)) return new VariableExpression(Previous.Text, Previous.Line);
        if (Match(TokenKind.LeftParen))
        {
            var expression = ParseExpression();
            Consume(TokenKind.RightParen, "Ожидалась ')' после выражения.");
            return expression;
        }
        throw Error(Current, "Ожидалось выражение.");
    }

    private Token Current => _tokens[_position];
    private Token Previous => _tokens[_position - 1];
    private bool Check(TokenKind kind) => Current.Kind == kind;
    private bool CheckNext(TokenKind kind) => _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == kind;

    private bool Match(params TokenKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            if (!Check(kind)) continue;
            Advance();
            return true;
        }
        return false;
    }

    private Token Advance()
    {
        if (!Check(TokenKind.End)) _position++;
        return Previous;
    }

    private Token Consume(TokenKind kind, string message)
    {
        if (Check(kind)) return Advance();
        throw Error(Current, message);
    }

    private static CompileException Error(Token token, string message) => new(token.Line, message);
}
