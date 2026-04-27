using System;
using uniffi.traits;

namespace UniffiCS.BindingTests;

class OurButton : Button {
    public String Name() {
        return "c#";
    }
}

public class TestTraits
{
    [Fact]
    public void TraitsWorking()
    {
        string[] validNames = ["go", "stop"];
        foreach (var button in TraitsMethods.GetButtons())
        {
            var name = button.Name();
            Assert.Contains(name, validNames);
            Assert.Equal(TraitsMethods.Press(button).Name(), name);
        }
    }

    [Fact]
    public void TraitsWorkingWithForeign()
    {
        var button = new OurButton();
        Assert.Equal("c#", button.Name());
        Assert.Equal("c#", TraitsMethods.Press(button).Name());
    }

}
