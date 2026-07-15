// Vendored from https://github.com/0blu/PhotonPackageParser (MIT) - see THIRD-PARTY-NOTICES.md
namespace PhotonPackageParser
{
    internal class SegmentedPackage
    {
        public int TotalLength;
        public int BytesWritten;
        public byte[] TotalPayload = System.Array.Empty<byte>();
    }
}
