// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Implementation.Outlining;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Tagging;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Text.Tagging;
using Moq;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Tagging
{
    public class AsynchronousTaggerTests : TestBase
    {
        /// <summary>
        /// This hits a special codepath in the product that is optimized for more than 100 spans.
        /// I'm leaving this test here because it covers that code path (as shown by code coverage)
        /// </summary>
        [Fact]
        [WorkItem(530368)]
        public void LargeNumberOfSpans()
        {
            using (var workspace = CSharpWorkspaceFactory.CreateWorkspaceFromFile(@"class Program
{
    void M()
    {
        int z = 0;
        z = z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z +
            z + z + z + z + z + z + z + z + z + z;
    }
}"))
            {
                var tagProducer = new TestTagProducer(
                    (span, cancellationToken) =>
                    {
                        return new List<ITagSpan<TestTag>>() { new TagSpan<TestTag>(span, new TestTag()) };
                    });
                var asyncListener = new TaggerOperationListener();

                var notificationService = workspace.GetService<IForegroundNotificationService>();

                var eventSource = CreateEventSource();
                var taggerProvider = new TestTaggerProvider(
                    tagProducer,
                    eventSource,
                    workspace,
                    asyncListener,
                    notificationService);

                var document = workspace.Documents.First();
                var textBuffer = document.TextBuffer;
                var snapshot = textBuffer.CurrentSnapshot;
                var tagger = taggerProvider.CreateTagger<TestTag>(textBuffer);

                using (IDisposable disposable = (IDisposable)tagger)
                {
                    var spans = Enumerable.Range(0, 101).Select(i => new Span(i * 4, 1));
                    var snapshotSpans = new NormalizedSnapshotSpanCollection(snapshot, spans);

                    eventSource.SendUpdateEvent();

                    asyncListener.CreateWaitTask().PumpingWait();

                    var tags = tagger.GetTags(snapshotSpans);

                    Assert.Equal(1, tags.Count());
                }
            }
        }

        [Fact]
        public void TestSynchronousOutlining()
        {
            using (var workspace = CSharpWorkspaceFactory.CreateWorkspaceFromFile("class Program {\r\n\r\n}"))
            {
                var tagProvider = new OutliningTaggerProvider(
                    workspace.GetService<IForegroundNotificationService>(),
                    workspace.GetService<ITextEditorFactoryService>(),
                    workspace.GetService<IEditorOptionsFactoryService>(),
                    workspace.GetService<IProjectionBufferFactoryService>(),
                    (IEnumerable<Lazy<IAsynchronousOperationListener, FeatureMetadata>>)workspace.ExportProvider.GetExports<IAsynchronousOperationListener, FeatureMetadata>());

                var document = workspace.Documents.First();
                var textBuffer = document.TextBuffer;
                var tagger = tagProvider.CreateTagger<IOutliningRegionTag>(textBuffer);

                using (var disposable = (IDisposable)tagger)
                {
                    tagProvider.ComputeTagsSynchronouslyIfNoAsynchronousComputationHasCompleted = true;

                    // The very first all to get tags should return the single outlining span.
                    var tags = tagger.GetTags(new NormalizedSnapshotSpanCollection(textBuffer.CurrentSnapshot.GetFullSpan()));
                    Assert.Equal(1, tags.Count());
                }
            }
        }

        private static TestTaggerEventSource CreateEventSource()
        {
            return new TestTaggerEventSource();
        }

        private static Mock<IOptionService> CreateFeatureOptionsMock()
        {
            var featureOptions = new Mock<IOptionService>(MockBehavior.Strict);
            featureOptions.Setup(s => s.GetOption(EditorComponentOnOffOptions.Tagger)).Returns(true);
            return featureOptions;
        }

        private sealed class TaggerOperationListener : AsynchronousOperationListener
        {
        }

        private sealed class TestTag : TextMarkerTag
        {
            public TestTag() :
                base("Test")
            {
            }
        }

        private sealed class TestTagProducer : AbstractSingleDocumentTagProducer<TestTag>
        {
            public delegate List<ITagSpan<TestTag>> Callback(SnapshotSpan span, CancellationToken cancellationToken);

            private readonly Callback _produceTags;

            public TestTagProducer(Callback produceTags)
            {
                _produceTags = produceTags;
            }

            public override Task<IEnumerable<ITagSpan<TestTag>>> ProduceTagsAsync(Document document, SnapshotSpan snapshotSpan, int? caretPosition, CancellationToken cancellationToken)
            {
                return Task.FromResult<IEnumerable<ITagSpan<TestTag>>>(_produceTags(snapshotSpan, cancellationToken));
            }
        }

        private sealed class TestTaggerProvider : ITaggerProvider, IAsynchronousTaggerDataSource<TestTag>
        {
            private readonly ITaggerProvider _taggerProvider;
            private readonly TestTagProducer _tagProducer;
            private readonly ITaggerEventSource _eventSource;
            private readonly Workspace _workspace;
            private readonly bool _disableCancellation;

            public TaggerDelay? UIUpdateDelay => null;
            public bool RemoveTagsThatIntersectEdits => true;
            public SpanTrackingMode SpanTrackingMode => SpanTrackingMode.EdgeExclusive;
            public bool ComputeTagsSynchronouslyIfNoAsynchronousComputationHasCompleted => false;
            public IEnumerable<Option<bool>> Options => null;
            public IEnumerable<PerLanguageOption<bool>> PerLanguageOptions => null;

            public TestTaggerProvider(
                TestTagProducer tagProducer,
                ITaggerEventSource eventSource,
                Workspace workspace,
                IAsynchronousOperationListener asyncListener,
                IForegroundNotificationService notificationService,
                bool disableCancellation = false)
            {
                _taggerProvider = new AsynchronousTaggerProvider<TestTag>(this, asyncListener, notificationService);
                _tagProducer = tagProducer;
                _eventSource = eventSource;
                _workspace = workspace;
                _disableCancellation = disableCancellation;
            }

            public ITaggerEventSource CreateEventSource(ITextView textViewOpt, ITextBuffer subjectBuffer)
            {
                return _eventSource;
            }

            public ITagProducer<TestTag> CreateTagProducer()
            {
                return _tagProducer;
            }

            public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
            {
                return _taggerProvider.CreateTagger<T>(buffer);
            }
        }

        private sealed class TestTaggerEventSource : AbstractTaggerEventSource
        {
            public TestTaggerEventSource() :
                base(delay: TaggerDelay.NearImmediate)
            {
            }

            public override string EventKind
            {
                get
                {
                    return "Test";
                }
            }

            public void SendUpdateEvent()
            {
                this.RaiseChanged();
            }

            public override void Connect()
            {
            }

            public override void Disconnect()
            {
            }
        }
    }
}
