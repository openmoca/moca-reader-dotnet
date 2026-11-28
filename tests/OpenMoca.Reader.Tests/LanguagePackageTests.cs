using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Reading a package whose content comes in more than one language: what the corpus does not pin down.</summary>
public sealed class LanguagePackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("moca-language-package-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<PackageResult> ReadAsync(string manifest, params (string Path, string Text)[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "moca.json"), manifest);
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_dir, path))!);
            File.WriteAllText(Path.Combine(_dir, path), text);
        }

        return await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("translationOf")]
    [InlineData("editionOf")]
    public async Task ATranslationOrEditionRelation_IsACoreType_AndNeedsNoSigner(string type)
    {
        var result = await ReadAsync(
            $$"""{ "id": "https://example.com/fr", "version": "1.0.0", "title": "T", "relations": [{ "type": "{{type}}", "target": "https://example.com/en" }] }""",
            ("content/a.md", "---\ntype: Note\ntitle: A\n---\n\nA.\n"));

        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is DiagnosticCodes.R005RelationTypeUnknown or DiagnosticCodes.R004RelationUnauthorised);
    }

    [Fact]
    public async Task CaptionTracks_AreCheckedEntryByEntry_AndTheDescriptionStands()
    {
        var result = await ReadAsync(
            """{ "id": "https://example.com/talk", "version": "1.0.0", "title": "T" }""",
            ("media/talk.en.vtt", "WEBVTT\n\n00:00.000 --> 00:05.000\nWelcome.\n"),
            ("media/talk.fr.vtt", "WEBVTT\n\n00:00.000 --> 00:05.000\nBienvenue.\n"),
            ("content/talk.md", """
                ---
                type: Talk
                title: Talk
                sources:
                  - id: video
                    resource: https://media.example.com/talk.mp4
                  - id: transcript
                    resource: ../media/talk.en.vtt
                  - id: captions-fr
                    resource: ../media/talk.fr.vtt
                  - id: page
                    resource: https://example.com/talk.html
                moca:
                  media:
                    - source: video
                      type: video/mp4
                      transcript: transcript
                      captions:
                        - { source: captions-fr, language: fr }
                        - { source: transcript, language: en, kind: captions }
                        - { source: captions-fr, language: fr, kind: captions }
                        - { source: unknown, language: de }
                        - { source: page, language: en }
                        - { source: transcript, language: en, kind: karaoke }
                ---

                Welcome.
                """));

        var media = Assert.Single(new Library().Add(result).Citations().Single().Node.Media!);

        Assert.True(result.Valid);
        Assert.Equal("transcript", media.Transcript);
        Assert.Equal([new("captions-fr", "fr", "subtitles"), new("transcript", "en", "captions")], media.Captions!);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.C006EvidenceSourceUnknown);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == DiagnosticCodes.C005FieldInvalid && d.Message.Contains("caption track", StringComparison.Ordinal)));
    }
}
