namespace Dirused.Common;

public static class AclRights
{
    public static WriteRights FromMask(int mask)
    {
        if ((mask & 0x1F01FF) == 0x1F01FF || (mask & 0x10000000) != 0)
            return WriteRights.GenericAll;

        var rights = WriteRights.None;
        if ((mask & 0x2) != 0) rights |= WriteRights.Write;
        if ((mask & 0x4) != 0) rights |= WriteRights.Append;
        if ((mask & 0x10000) != 0) rights |= WriteRights.Delete;
        if ((mask & 0x40) != 0) rights |= WriteRights.DeleteChild;
        if ((mask & 0x40000) != 0) rights |= WriteRights.WriteDac;
        if ((mask & 0x80000) != 0) rights |= WriteRights.WriteOwner;
        if ((mask & 0x40000000) != 0) rights |= WriteRights.GenericWrite | WriteRights.Write | WriteRights.Append;
        return rights;
    }
}
