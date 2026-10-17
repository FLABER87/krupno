using Krupno.Core.Inference;

namespace Krupno.Core.Tests;

public class ModelCatalogTests
{
    private static ModelEntry Entry(string name) => new(name, $@"C:\models\{name}.onnx");

    [Fact]
    public void TheDenoiserIsNeverTheDefault()
    {
        // Alphabetically "scunet…" would come before the user's own "x4_…", but it is not an upscaler.
        var models = new[] { Entry("scunet_color_real_psnr_fp16"), Entry("x4_mine") };
        Assert.Equal("x4_mine", ModelCatalog.PickDefault(models)?.Name);
        Assert.Null(ModelCatalog.PickDefault([Entry("scunet_color_real_psnr_fp16")]));
        Assert.Equal(ModelCatalog.DefaultModelName,
            ModelCatalog.PickDefault([Entry("a_first"), Entry(ModelCatalog.DefaultModelName)])?.Name);
    }

    [Fact]
    public void TheReleasedDenoiserIsFoundFirst()
    {
        Assert.True(ModelCatalog.IsDenoiser(Entry("SCUNet_Color_Real_PSNR_fp16")));
        Assert.True(ModelCatalog.IsDenoiser(Entry("scunet_color_real_psnr")));
        Assert.False(ModelCatalog.IsDenoiser(Entry(ModelCatalog.DefaultModelName)));

        Assert.Null(ModelCatalog.FindDenoiser([Entry(ModelCatalog.DefaultModelName)]));
        Assert.Equal("scunet_color_real_psnr", ModelCatalog.FindDenoiser([Entry("scunet_color_real_psnr")])?.Name);
        Assert.Equal(ModelCatalog.DenoiseModelName,
            ModelCatalog.FindDenoiser([Entry("scunet_color_real_psnr"), Entry(ModelCatalog.DenoiseModelName)])?.Name);
    }
}
