using Dirused.Common;
using Xunit;

namespace Dirused.Tests.Portable;

public class AclRightsTests
{
    [Theory]
    [InlineData(0x1F01FF, WriteRights.GenericAll)]
    [InlineData(0x10000000, WriteRights.GenericAll)]
    [InlineData(0x301BF, WriteRights.Write | WriteRights.Append | WriteRights.Delete)]
    [InlineData(0x1200A9, WriteRights.None)]
    [InlineData(0x4, WriteRights.Append)]
    [InlineData(0x2, WriteRights.Write)]
    [InlineData(0x40, WriteRights.DeleteChild)]
    [InlineData(0x10000, WriteRights.Delete)]
    [InlineData(0x40000, WriteRights.WriteDac)]
    [InlineData(0x80000, WriteRights.WriteOwner)]
    [InlineData(0x40000000, WriteRights.GenericWrite | WriteRights.Write | WriteRights.Append)]
    [InlineData(unchecked((int)0x5F01FF), WriteRights.GenericAll)]
    public void FromMaskMapsSpecifiedRights(int mask, WriteRights expected) => Assert.Equal(expected, AclRights.FromMask(mask));
}
