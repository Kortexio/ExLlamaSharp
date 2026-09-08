using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.Tests;

public class HuggingFaceCatalogFilterTests
{
    private static HuggingFaceModelHit Hit(
        string repoId,
        string? tags = null,
        string? pipeline = "text-generation",
        long? size = null,
        bool? safetensors = null,
        bool? tokenizer = null,
        bool? config = null)
    {
        var display = repoId.Contains('/') ? repoId[(repoId.LastIndexOf('/') + 1)..] : repoId;
        return new HuggingFaceModelHit
        {
            RepoId = repoId,
            DisplayName = display,
            Tags = tags ?? "exl3,text-generation",
            PipelineTag = pipeline,
            SizeBytes = size,
            HasSafetensors = safetensors,
            HasTokenizer = tokenizer,
            HasConfig = config,
        };
    }

    [Theory]
    [InlineData("turboderp/Qwen3.5-9B-exl3", "exl3")]
    [InlineData("turboderp/Qwen3-8B-exl3", "exl3,text-generation")]
    [InlineData("someone/Llama-3.2-1B-Instruct-exl3", "exl3")]
    [InlineData("org/Qwen3-VL-8B-Instruct-exl3", "exl3,image-text-to-text")]
    public void Accepts_standalone_exl3_chat_models(string repo, string tags)
    {
        var pipeline = tags.Contains("image-text-to-text") ? "image-text-to-text" : "text-generation";
        Assert.True(HuggingFaceCatalogService.IsStandaloneExl3Candidate(Hit(repo, tags, pipeline)));
    }

    [Theory]
    [InlineData("Mia-AiLab/Qwen3.8-27B-DFlash2-EXL3-5.0bpw", "exl3,draft-model,dflash2")]
    [InlineData("incoai/Qwen3.8-27B-DFlash2", "draft-model,dflash2")]
    [InlineData("org/SomeModel-exl3-draft", "exl3,draft-model")]
    [InlineData("org/model-draft-exl3", "exl3")]
    [InlineData("org/speculative-draft-exl3", "exl3")]
    public void Rejects_draft_and_dflash_repos(string repo, string tags)
    {
        Assert.False(HuggingFaceCatalogService.IsStandaloneExl3Candidate(Hit(repo, tags)));
    }

    [Fact]
    public void Rejects_non_exl3_repos()
    {
        Assert.False(HuggingFaceCatalogService.IsStandaloneExl3Candidate(
            Hit("org/Llama-3-8B-GGUF", "gguf", "text-generation")));
    }

    [Fact]
    public void Rejects_blocked_pipelines()
    {
        Assert.False(HuggingFaceCatalogService.IsStandaloneExl3Candidate(
            Hit("org/embed-exl3", "exl3", "feature-extraction")));
    }

    [Fact]
    public void Weight_gate_allows_pending_enrichment()
    {
        var hit = Hit("turboderp/Qwen3-8B-exl3");
        Assert.True(HuggingFaceCatalogService.PassesWeightGate(hit));
    }

    [Fact]
    public void Weight_gate_rejects_missing_tokenizer_like_dflash()
    {
        var hit = Hit(
            "Mia-AiLab/Qwen3.8-27B-DFlash2-EXL3-5.0bpw",
            "exl3,draft-model",
            size: 1_400_000_000,
            safetensors: true,
            tokenizer: false,
            config: true);
        // Draft name already fails standalone; weight gate also fails without tokenizer.
        Assert.False(HuggingFaceCatalogService.PassesWeightGate(hit));
    }

    [Fact]
    public void Weight_gate_rejects_readme_only_clone()
    {
        var hit = Hit(
            "org/Qwen3-8B-exl3",
            "exl3",
            size: 2_000_000,
            safetensors: false,
            tokenizer: true,
            config: true);
        Assert.False(HuggingFaceCatalogService.PassesWeightGate(hit));
    }

    [Fact]
    public void Weight_gate_accepts_complete_exl3_tree()
    {
        var hit = Hit(
            "turboderp/Qwen3.5-9B-exl3",
            "exl3",
            size: 7_000_000_000,
            safetensors: true,
            tokenizer: true,
            config: true);
        Assert.True(HuggingFaceCatalogService.PassesWeightGate(hit));
        Assert.True(HuggingFaceCatalogService.IsStandaloneExl3Candidate(hit));
    }

    [Fact]
    public void FilterLoadableHits_drops_drafts()
    {
        var list = new[]
        {
            Hit("turboderp/Qwen3-8B-exl3", "exl3"),
            Hit("Mia-AiLab/Qwen3.8-27B-DFlash2-EXL3-5.0bpw", "exl3,dflash2,draft-model"),
        };
        var filtered = HuggingFaceCatalogService.FilterLoadableHits(list);
        Assert.Single(filtered);
        Assert.Equal("turboderp/Qwen3-8B-exl3", filtered[0].RepoId);
    }
}
