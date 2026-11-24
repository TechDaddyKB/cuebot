namespace Cuebot.Speech;

/// <summary>One Python wheel of the voice engine, with the SHA-256 and size it must have.</summary>
public sealed record VoiceWheel(string FileName, string Url, string Sha256, long Size);

/// <summary>
/// The exact packages the voice engine installs into its own Python environment, per python-build-standalone target
/// (see Cuebot.Scripting's PinnedPython): sherpa-onnx (Apache-2.0) and its native core from PyPI, and piper_phonemize
/// (GPL-3.0, espeak-ng) from its GitHub release. Pinned and checked like Python itself, so a changed or tampered
/// download is refused. Installed with pip --no-index --no-deps: nothing else is fetched.
/// </summary>
public static class VoiceEnginePins
{
    public const string SherpaVersion = "2.0.3";
    public const string PhonemizeVersion = "1.6.1";

    /// <summary>Written next to the environment once installed; a different value means "install again".</summary>
    public const string Stamp = $"sherpa-onnx {SherpaVersion}; piper_phonemize {PhonemizeVersion}";

    private static readonly Dictionary<string, VoiceWheel[]> Wheels = new()
    {
        ["x86_64-pc-windows-msvc"] =
        [
            new("sherpa_onnx_core-2.0.3-py3-none-win_amd64.whl", "https://files.pythonhosted.org/packages/86/60/267823e104a5856b38601784ae6ca388cd00f219b49c8e78de380ceb63b0/sherpa_onnx_core-2.0.3-py3-none-win_amd64.whl",
                "798ba3803b6461bac9eca5a7f3927f04e7a54173155e3f0479e6abd89bb4bc59", 17216593),
            new("sherpa_onnx-2.0.3-cp313-cp313-win_amd64.whl", "https://files.pythonhosted.org/packages/d0/fd/7d0cc54be0e809019745739cef60e1967c3b9db78ee44f31bbdd3c018ae2/sherpa_onnx-2.0.3-cp313-cp313-win_amd64.whl",
                "472329ad2f8ca5e2d0e517cba279d61c86c23729060920abe6a5816fb6e50fab", 2474590),
            new("piper_phonemize-1.6.1-cp313-cp313-win_amd64.whl", "https://github.com/csukuangfj/piper-phonemize/releases/download/v1.6.1/piper_phonemize-1.6.1-cp313-cp313-win_amd64.whl",
                "1c8bee92d4ab75a8c67142a6b858f217b3c6e5fbef560aaee7b42e8f533b2781", 10003308),
        ],
        ["aarch64-pc-windows-msvc"] =
        [
            new("sherpa_onnx_core-2.0.3-py3-none-win_arm64.whl", "https://files.pythonhosted.org/packages/65/a1/a9d842aef37d5af90b0b25c8db47f812356a2b25fd7b34fb895a66a8b1aa/sherpa_onnx_core-2.0.3-py3-none-win_arm64.whl",
                "03efdd29b733c11dadb4764f009e6aecc78a865bdeae16ab2d1d7ecc3e54272f", 16364519),
            new("sherpa_onnx-2.0.3-cp313-cp313-win_arm64.whl", "https://files.pythonhosted.org/packages/75/e0/e40aeaa87a7aec59bbb50d80287ba338db2cedfe7bb47e33bfc2f1445591/sherpa_onnx-2.0.3-cp313-cp313-win_arm64.whl",
                "f6b9e3ae7cef3f8672a169bf4cf7a9f3f7d5d98f8961beceb694a066e3036532", 2425370),
            new("piper_phonemize-1.6.1-cp313-cp313-win_arm64.whl", "https://github.com/csukuangfj/piper-phonemize/releases/download/v1.6.1/piper_phonemize-1.6.1-cp313-cp313-win_arm64.whl",
                "b05b3ef3fb1e2e84f37feaec789b8fe245c9b5f77da32748631ce5514e95533e", 9925410),
        ],
        ["x86_64-unknown-linux-gnu"] =
        [
            new("sherpa_onnx_core-2.0.3-py3-none-manylinux2014_x86_64.whl", "https://files.pythonhosted.org/packages/44/a8/339ae6e5edd700c0e3a5a5dbfa64136d186bf1c1c795295fab0df2b57bf7/sherpa_onnx_core-2.0.3-py3-none-manylinux2014_x86_64.whl",
                "3c4e54ed9ac03ef8cc9b7340dc18a1203017bdd0e4ecc9923562560006d881e0", 10943178),
            new("sherpa_onnx-2.0.3-cp313-cp313-manylinux2014_x86_64.manylinux_2_17_x86_64.whl", "https://files.pythonhosted.org/packages/e6/6c/635eaceef707b5c085d47e2cf86f639a84c7b94935bbf27e4b2666d388de/sherpa_onnx-2.0.3-cp313-cp313-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
                "fd9ae354b544781399efb3302d5a822627b8be3971ea7671198af490dbb38408", 4644006),
            new("piper_phonemize-1.6.1-cp313-cp313-manylinux2014_x86_64.manylinux_2_17_x86_64.whl", "https://github.com/csukuangfj/piper-phonemize/releases/download/v1.6.1/piper_phonemize-1.6.1-cp313-cp313-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
                "fe55c8bdd2f9a26a2f0cc9f2131dddd4f608da0135edf6dd56490b2473c439ed", 9728406),
        ],
        ["aarch64-unknown-linux-gnu"] =
        [
            new("sherpa_onnx_core-2.0.3-py3-none-manylinux2014_aarch64.whl", "https://files.pythonhosted.org/packages/c0/20/cff1fd963b27df73e7ebb7c40a5640d6932361b9223df6de72dffa637f44/sherpa_onnx_core-2.0.3-py3-none-manylinux2014_aarch64.whl",
                "6db4c9e70d49ba38f4cd54c121a05bb63b6085b42867216086144e445ac603c1", 13629580),
            new("sherpa_onnx-2.0.3-cp313-cp313-manylinux2014_aarch64.manylinux_2_17_aarch64.whl", "https://files.pythonhosted.org/packages/47/9e/7db81e728ac053ec859844b0afab28b12d7b76ae4bc810d0472cf3736e94/sherpa_onnx-2.0.3-cp313-cp313-manylinux2014_aarch64.manylinux_2_17_aarch64.whl",
                "e41466de65b2bded07f40963cdb53f96010fe04c307b27a4f9c4e538c4666326", 4407897),
            new("piper_phonemize-1.6.1-cp313-cp313-manylinux2014_aarch64.manylinux_2_17_aarch64.whl", "https://github.com/csukuangfj/piper-phonemize/releases/download/v1.6.1/piper_phonemize-1.6.1-cp313-cp313-manylinux2014_aarch64.manylinux_2_17_aarch64.whl",
                "fe0b6eaf6e985dc83f28de5abe330a93d97313251fcc147759919c042517c129", 9702611),
        ],
        ["x86_64-apple-darwin"] =
        [
            new("sherpa_onnx_core-2.0.3-py3-none-macosx_10_15_x86_64.whl", "https://files.pythonhosted.org/packages/ac/41/adb2da4fc7c89da4e6dd93a6538a3612aa169af27972db0870e711e68e42/sherpa_onnx_core-2.0.3-py3-none-macosx_10_15_x86_64.whl",
                "6a86b5dc271aca8aa69e8311cb6f1c0726cb6c0f94f1547b14e3eb9f9375c9fb", 11231624),
            new("sherpa_onnx-2.0.3-cp313-cp313-macosx_10_15_x86_64.whl", "https://files.pythonhosted.org/packages/d2/1f/62e6698e0537dec7d6e8c013fa7a964580b62a92250ed8ecd33754642289/sherpa_onnx-2.0.3-cp313-cp313-macosx_10_15_x86_64.whl",
                "08d75133ba566ced86f666a6897ddcd0fc0d3dec306af03002637ae6a6a07499", 2672868),
            new("piper_phonemize-1.6.1-cp313-cp313-macosx_10_14_x86_64.whl", "https://github.com/csukuangfj/piper-phonemize/releases/download/v1.6.1/piper_phonemize-1.6.1-cp313-cp313-macosx_10_14_x86_64.whl",
                "cd729ab25979d75d1f7c5e39e4fe23677c5dcd88633157cbea1538c2c316d8d1", 9606438),
        ],
        ["aarch64-apple-darwin"] =
        [
            new("sherpa_onnx_core-2.0.3-py3-none-macosx_11_0_arm64.whl", "https://files.pythonhosted.org/packages/f1/36/0c887684a033f6de0a5e3c0e7a244330fd1fb1a89a9917da70aff4ff6e59/sherpa_onnx_core-2.0.3-py3-none-macosx_11_0_arm64.whl",
                "ca19ab0ae8c78b9a0a987ebd1aa69b73e3b7490733646e831db46260dab85316", 9871853),
            new("sherpa_onnx-2.0.3-cp313-cp313-macosx_11_0_arm64.whl", "https://files.pythonhosted.org/packages/13/3d/55d347a5c03d011a0da27abdc7387eee1863e8125a3531b8d453683685fd/sherpa_onnx-2.0.3-cp313-cp313-macosx_11_0_arm64.whl",
                "5d8794b2ce86b5bc76f7e8a09ed13c174be5241b5f0428de76843c2739799b40", 2434878),
            new("piper_phonemize-1.6.1-cp313-cp313-macosx_11_0_arm64.whl", "https://github.com/csukuangfj/piper-phonemize/releases/download/v1.6.1/piper_phonemize-1.6.1-cp313-cp313-macosx_11_0_arm64.whl",
                "28b38b606fe6b16c0db456d2f258eb6928eb8c29715e9a85c155c3b61df4eb1a", 9585938),
        ],
    };

    public static IReadOnlyList<VoiceWheel>? For(string? target) => target is not null && Wheels.TryGetValue(target, out var wheels) ? wheels : null;

    public static IEnumerable<string> Targets => Wheels.Keys;
}
