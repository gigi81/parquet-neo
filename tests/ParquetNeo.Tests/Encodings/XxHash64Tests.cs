using ParquetNeo.Encodings;

namespace ParquetNeo.Tests.Encodings;

public class XxHash64Tests
{
    [Test]
    [Arguments("", 0xEF46DB3751D8E999UL)]
    [Arguments("a", 0xD24EC4F1A98C6E5BUL)]
    [Arguments("abc", 0x44BC2CF5AD770999UL)]
    [Arguments("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    public async Task TheHash_IsTheOneOfTheReferenceImplementation(string text, ulong expected)
    {
        await Assert.That(XxHash64.Hash(System.Text.Encoding.UTF8.GetBytes(text))).IsEqualTo(expected);
    }

    [Test]
    public async Task EveryLength_HasAHash_ThatDependsOnEveryByte()
    {
        var data = new byte[100];
        new Random(3).NextBytes(data);
        var seen = new HashSet<ulong>();
        for (int length = 0; length <= data.Length; length++)
        {
            seen.Add(XxHash64.Hash(data.AsSpan(0, length)));
        }

        await Assert.That(seen.Count).IsEqualTo(data.Length + 1);
        for (int index = 0; index < data.Length; index++)
        {
            var changed = (byte[])data.Clone();
            changed[index] ^= 1;
            await Assert.That(XxHash64.Hash(changed)).IsNotEqualTo(XxHash64.Hash(data));
        }
    }
}
