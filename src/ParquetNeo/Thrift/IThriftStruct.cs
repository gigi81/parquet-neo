namespace ParquetNeo.Thrift;

/// <summary>A Thrift struct that can read and write itself with the compact protocol.</summary>
internal interface IThriftStruct<TSelf> where TSelf : IThriftStruct<TSelf>
{
    static abstract TSelf Read(ref ThriftCompactReader reader);

    void Write(ref ThriftCompactWriter writer);
}