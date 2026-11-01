namespace ParquetNeo.Thrift;

/// <summary>Wire type ids of the Thrift compact protocol.</summary>
internal enum CompactType : byte
{
    Stop = 0,
    BoolTrue = 1,
    BoolFalse = 2,
    I8 = 3,
    I16 = 4,
    I32 = 5,
    I64 = 6,
    Double = 7,
    Binary = 8,
    List = 9,
    Set = 10,
    Map = 11,
    Struct = 12,
}
