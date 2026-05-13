using Bond;
using Bond.Expressions;
using Bond.IO.Unsafe;
using Bond.Protocols;
using BondDump;
using Microsoft.CSharp;
using System.CodeDom;
using System.Linq.Expressions;

using CSharpCodeProvider codeProvider = new();

var deserializeExpr = (LambdaExpression)GenerateDeserialize<CompactBinaryReader<InputBuffer>>(typeof(ValueSet))
    .Single()
    .Reduce();

var deserialize = CodeDomExpressionVisitor.Instance.Convert(deserializeExpr, "Deserialize");

var serializeExpr = (LambdaExpression)GenerateSerialize<object, CompactBinaryWriter<OutputBuffer>>(typeof(ValueSet))
    .Single()
    .Reduce();

var serialize = CodeDomExpressionVisitor.Instance.Convert(serializeExpr, "Serialize");

CodeCompileUnit unit = new()
{
    Namespaces = {
        new CodeNamespace("ShortDev.Microsoft.ConnectedDevices.Serialization")
        {
            Types = {
                new CodeTypeDeclaration("ValueSetHelper") {
                    Members =
                    {
                        serialize,
                        deserialize,
                    }
                }
            }
        }
    }
};

Console.WriteLine(codeProvider.Compile(unit));
File.WriteAllText("bond-dump.cs", codeProvider.Compile(unit));

// ==================================================================================================================================================================================== //

static IEnumerable<Expression<Action<TSchema, TWriter>>> GenerateSerialize<TSchema, TWriter>(Type schema)
{
    var TDeserializerTransform = typeof(Serializer<>).Assembly.GetType("Bond.Expressions.SerializerGeneratorFactory`2", throwOnError: true)!
        .MakeGenericType(typeof(TSchema), typeof(TWriter));

    Expression<Action<TSchema, TWriter, int>> deferredSerialize = (a, b, c) => a.ToString();
    var serializerGenerator = (ISerializerGenerator<TSchema, TWriter>)TDeserializerTransform.GetMethod("Create")!
        .MakeGenericMethod(typeof(Type))
        .Invoke(null, new object[] { deferredSerialize, schema, /*inlineNested*/ true })!;

    ObjectParser parser = new(schema);
    return serializerGenerator.Generate(parser);
}

static IEnumerable<Expression<Func<R, object>>> GenerateDeserialize<R>(Type schema)
{
    var TDeserializerTransform = typeof(Deserializer<>).Assembly.GetType("Bond.Expressions.DeserializerTransform`1", throwOnError: true)!
        .MakeGenericType(typeof(R));

    Expression<Func<R, int, object>> deferredDeserialize = (r, i) => null!;
    var deserializerTransform = Activator.CreateInstance(TDeserializerTransform, deferredDeserialize, null, /*inlineNested*/ true);

    dynamic result = TDeserializerTransform.GetMethod("Generate")!
        .Invoke(deserializerTransform, [ParserFactory<R>.Create(schema), schema])!;
    return result;
}
