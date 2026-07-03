using HospitalAccess.Application.Qr;
using Xunit;

namespace HospitalAccess.Tests.Qr;

public class VisitorCardNumberTests
{
    [Fact]
    public void FromUserCode_RoundTripsThroughToUserCode()
    {
        const uint userCode = 123456u;
        var card = VisitorCardNumber.FromUserCode(userCode);

        Assert.Equal(VisitorCardNumber.Length, card.Length);
        Assert.Equal(userCode, VisitorCardNumber.ToUserCode(card));
    }

    [Fact]
    public void FromUserCode_PadsRemainingBytesWithZero()
    {
        var card = VisitorCardNumber.FromUserCode(1u);
        Assert.All(card[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ToUserCode_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => VisitorCardNumber.ToUserCode(new byte[8]));
    }

    [Fact]
    public void FromUserCode_HandlesMaxValue()
    {
        var card = VisitorCardNumber.FromUserCode(uint.MaxValue);
        Assert.Equal(uint.MaxValue, VisitorCardNumber.ToUserCode(card));
    }
}
