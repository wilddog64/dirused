using System.ComponentModel;
using System.Runtime.InteropServices;
using Dirused.Core.Native;
namespace Dirused.Core.Targets;
public record NodeShare(string Name, uint Type);
public interface INodeShares { IEnumerable<NodeShare> Enumerate(string node); }
public sealed class NodeShares : INodeShares
{
    public IEnumerable<NodeShare> Enumerate(string node)
    {
        var shares = new List<NodeShare>(); uint resume = 0; int status;
        do
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                status = NetApi32.NetShareEnum(node, 1, out buffer, uint.MaxValue, out uint count, out _, ref resume);
                if (status != 0 && status != 234) throw new Win32Exception(status);
                int size = Marshal.SizeOf<NetApi32.ShareInfo1>();
                for (int i = 0; i < count; i++)
                {
                    var share = Marshal.PtrToStructure<NetApi32.ShareInfo1>(IntPtr.Add(buffer, i * size));
                    shares.Add(new NodeShare(Marshal.PtrToStringUni(share.Name) ?? "", share.Type));
                }
            }
            finally { if (buffer != IntPtr.Zero) NetApi32.NetApiBufferFree(buffer); }
        } while (status == 234);
        return shares;
    }
}
