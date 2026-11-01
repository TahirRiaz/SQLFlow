using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SqlFlow.Yaml;

/// <summary>
/// Where a document holds a value of any shape (a parameter, a template constant), YamlDotNet's unquoted-scalar option types a
/// decimal as a single-precision <see cref="float"/> whenever one will hold it, which moves every value past about seven
/// significant digits (2128.7232 reads as 2128.7231) and turns 0.1 into 0.100000001 once it is widened. An unquoted decimal is
/// read in double precision instead, which keeps what the document wrote. Only an untyped plain scalar written as a decimal is
/// taken: a quoted value stays text, an integer keeps its integer type, and anything a typed property reads is untouched.
/// </summary>
public sealed partial class ExactDecimalNodeDeserializer : INodeDeserializer
{
    /// <inheritdoc />
    public bool Deserialize(
        IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(expectedType);
        value = null;
        if (expectedType != typeof(object)
            || !reader.Accept<Scalar>(out var scalar)
            || scalar.Style != ScalarStyle.Plain
            || !scalar.Tag.IsEmpty
            || !DecimalForm().IsMatch(scalar.Value)
            || !double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
        {
            return false;
        }

        reader.Consume<Scalar>();
        value = number;
        return true;
    }

    [GeneratedRegex(@"^[-+]?(\d+\.\d*|\.\d+)([eE][-+]?\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalForm();
}

/// <summary>The builder step for <see cref="ExactDecimalNodeDeserializer"/>.</summary>
public static class ExactDecimals
{
    /// <summary>Reads an unquoted decimal in double precision where a document holds a value of any shape. It goes with
    /// <see cref="DeserializerBuilder.WithAttemptingUnquotedStringTypeDeserialization"/>, ahead of what that option reads.</summary>
    public static DeserializerBuilder WithExactDecimals(this DeserializerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithNodeDeserializer(new ExactDecimalNodeDeserializer(), where => where.OnTop());
    }
}
