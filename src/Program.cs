using Bond;
using Bond.Expressions;
using Bond.IO.Unsafe;
using Bond.Protocols;
using BondDump;
using Microsoft.CSharp;
using ShortDev.Microsoft.ConnectedDevices.Serialization;
using System.CodeDom;
using System.Linq.Expressions;

using CSharpCodeProvider codeProvider = new();

CodeTypeDeclaration helperClass = new("ValueSetHelper");

foreach (var (index, deserializeExpr) in GenerateDeserialize<CompactBinaryReader<InputBuffer>>(typeof(ValueSet)).Index())
{
    var deserialize = CodeDomExpressionVisitor.Instance.Convert(deserializeExpr, helperClass, index switch
    {
        0 => "Deserialize",
        _ => $"Deserialize{index}"
    });
    helperClass.Members.Add(deserialize);
}

foreach (var (index, serializeExpr) in GenerateSerialize<object, CompactBinaryWriter<OutputBuffer>>(typeof(ValueSet)).Index())
{
    var serialize = CodeDomExpressionVisitor.Instance.Convert(serializeExpr, helperClass, index switch
    {
        0 => "Serialize",
        _ => $"Serialize{index}"
    });
    helperClass.Members.Add(serialize);
}

CodeCompileUnit unit = new()
{
    Namespaces = {
        new CodeNamespace("ShortDev.Microsoft.ConnectedDevices.Serialization")
        {
            Types = {
                helperClass
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

    var valueArg = Expression.Parameter(typeof(TSchema), "value");
    var writerArg = Expression.Parameter(typeof(TWriter), "writer");
    var indexArg = Expression.Parameter(typeof(int), "index");
    var deferredSerialize = Expression.Lambda<Action<TSchema, TWriter, int>>(
        new InvokeRecursiveExpression(indexArg, [valueArg, writerArg]),
        valueArg,
        writerArg,
        indexArg
    );
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

    var readerArg = Expression.Parameter(typeof(R), "reader");
    var indexArg = Expression.Parameter(typeof(int), "index");
    var deferredDeserialize = Expression.Lambda<Func<R, int, object>>(
        new InvokeRecursiveExpression(indexArg, [readerArg]),
        readerArg,
        indexArg
    );
    var deserializerTransform = Activator.CreateInstance(TDeserializerTransform, deferredDeserialize, null, /*inlineNested*/ true);

    dynamic result = TDeserializerTransform.GetMethod("Generate")!
        .Invoke(deserializerTransform, [ParserFactory<R>.Create(schema), schema])!;
    return result;
}
