using Microsoft.ML.OnnxRuntime.Tensors;
using Pickles_Playlist_Editor.Tools;
using Xunit;

namespace ScdAudioTests;

public class AiScoreSignatureTests
{
    private static Dictionary<string, (TensorElementType, int[])> Io(string name, TensorElementType t, params int[] dims) => new() { [name] = (t, dims) };

    [Fact] // [EDGE-ai-score-16] the expected model passes, with a fixed or symbolic batch
    public void ValidateSignature_AcceptsTheModel()
    {
        Assert.Null(SonicsModel.ValidateSignature(Io("audio", TensorElementType.Float, 1, 1_920_000), Io("logit", TensorElementType.Float, 1, 1)));
        Assert.Null(SonicsModel.ValidateSignature(Io("audio", TensorElementType.Float, -1, 1_920_000), Io("logit", TensorElementType.Float, 1)));
    }

    [Fact] // [EDGE-ai-score-16] anything else makes the AI score unavailable instead of producing nonsense
    public void ValidateSignature_RejectsWrongNamesOrShapes()
    {
        var okOut = Io("logit", TensorElementType.Float, 1, 1);
        Assert.Contains("no 'audio'", SonicsModel.ValidateSignature(Io("wave", TensorElementType.Float, 1, 1_920_000), okOut));
        Assert.Contains("not float", SonicsModel.ValidateSignature(Io("audio", TensorElementType.Double, 1, 1_920_000), okOut));
        Assert.Contains("[1, 960000]", SonicsModel.ValidateSignature(Io("audio", TensorElementType.Float, 1, 960_000), okOut));
        Assert.Contains("[2, 1920000]", SonicsModel.ValidateSignature(Io("audio", TensorElementType.Float, 2, 1_920_000), okOut));
        var okIn = Io("audio", TensorElementType.Float, 1, 1_920_000);
        Assert.Contains("no 'logit'", SonicsModel.ValidateSignature(okIn, Io("probs", TensorElementType.Float, 1)));
        Assert.Contains("not one value", SonicsModel.ValidateSignature(okIn, Io("logit", TensorElementType.Float, 1, 2)));
    }
}
