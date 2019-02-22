﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using Microsoft.AspNetCore.Razor.LanguageServer.ProjectSystem;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace Microsoft.AspNetCore.Razor.LanguageServer
{
    internal class RazorCompletionEndpoint : ICompletionHandler, ICompletionResolveHandler
    {
        private CompletionCapability _capability;
        private readonly ILogger _logger;
        private readonly ForegroundDispatcher _foregroundDispatcher;
        private readonly DocumentResolver _documentResolver;
        private readonly RazorCompletionFactsService _completionFactsService;
        private readonly TagHelperCompletionService _tagHelperCompletionService;
        private readonly TagHelperDescriptionFactory _tagHelperDescriptionFactory;

        public RazorCompletionEndpoint(
            ForegroundDispatcher foregroundDispatcher,
            DocumentResolver documentResolver,
            RazorCompletionFactsService completionFactsService,
            TagHelperCompletionService tagHelperCompletionService,
            TagHelperDescriptionFactory tagHelperDescriptionFactory,
            ILoggerFactory loggerFactory)
        {
            if (foregroundDispatcher == null)
            {
                throw new ArgumentNullException(nameof(foregroundDispatcher));
            }

            if (documentResolver == null)
            {
                throw new ArgumentNullException(nameof(documentResolver));
            }

            if (completionFactsService == null)
            {
                throw new ArgumentNullException(nameof(completionFactsService));
            }

            if (tagHelperCompletionService == null)
            {
                throw new ArgumentNullException(nameof(tagHelperCompletionService));
            }

            if (tagHelperDescriptionFactory == null)
            {
                throw new ArgumentNullException(nameof(tagHelperDescriptionFactory));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _foregroundDispatcher = foregroundDispatcher;
            _documentResolver = documentResolver;
            _completionFactsService = completionFactsService;
            _tagHelperCompletionService = tagHelperCompletionService;
            _tagHelperDescriptionFactory = tagHelperDescriptionFactory;
            _logger = loggerFactory.CreateLogger<RazorCompletionEndpoint>();
        }

        public void SetCapability(CompletionCapability capability)
        {
            _capability = capability;
        }

        public async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
        {
            _foregroundDispatcher.AssertBackgroundThread();

            var document = await Task.Factory.StartNew(() =>
            {
                _documentResolver.TryResolveDocument(request.TextDocument.Uri.AbsolutePath, out var documentSnapshot);

                return documentSnapshot;
            }, CancellationToken.None, TaskCreationOptions.None, _foregroundDispatcher.ForegroundScheduler);

            var codeDocument = await document.GetGeneratedOutputAsync();

            if (codeDocument.IsUnsupported())
            {
                return new CompletionList(isIncomplete: false);
            }

            var syntaxTree = codeDocument.GetSyntaxTree();

            var sourceText = await document.GetTextAsync();
            var linePosition = new LinePosition((int)request.Position.Line, (int)request.Position.Character);
            var hostDocumentIndex = sourceText.Lines.GetPosition(linePosition);
            var location = new SourceSpan(hostDocumentIndex, 0);

            var directiveCompletionItems = _completionFactsService.GetCompletionItems(syntaxTree, location);

            _logger.LogTrace($"Found {directiveCompletionItems.Count} directive completion items.");

            var completionItems = new List<CompletionItem>();
            foreach (var razorCompletionItem in directiveCompletionItems)
            {
                if (razorCompletionItem.Kind != RazorCompletionItemKind.Directive)
                {
                    // Don't support any other types of completion kinds other than directives.
                    continue;
                }

                var directiveCompletionItem = new CompletionItem()
                {
                    Label = razorCompletionItem.DisplayText,
                    InsertText = razorCompletionItem.InsertText,
                    Detail = razorCompletionItem.Description,
                    Documentation = razorCompletionItem.Description,
                    FilterText = razorCompletionItem.DisplayText,
                    SortText = razorCompletionItem.DisplayText,
                    Kind = CompletionItemKind.Struct,
                };

                completionItems.Add(directiveCompletionItem);
            }

            var tagHelperCompletionItems = _tagHelperCompletionService.GetCompletionsAt(location, codeDocument);

            _logger.LogTrace($"Found {tagHelperCompletionItems.Count} TagHelper completion items.");

            completionItems.AddRange(tagHelperCompletionItems);

            var completionList = new CompletionList(completionItems, isIncomplete: false);

            return completionList;
        }

        public CompletionRegistrationOptions GetRegistrationOptions()
        {
            return new CompletionRegistrationOptions()
            {
                DocumentSelector = RazorDefaults.Selector,
                ResolveProvider = true,
                TriggerCharacters = new Container<string>("@", "<"),
            };
        }

        public bool CanResolve(CompletionItem completionItem)
        {
            if (completionItem.IsTagHelperElementCompletion() ||
                completionItem.IsTagHelperAttributeCompletion())
            {
                return true;
            }

            return false;
        }

        public Task<CompletionItem> Handle(CompletionItem completionItem, CancellationToken cancellationToken)
        {
            string markdown = null;
            if (completionItem.IsTagHelperElementCompletion())
            {
                var descriptionInfo = completionItem.GetElementDescriptionInfo();
                _tagHelperDescriptionFactory.TryCreateDescription(descriptionInfo, out markdown);
            }

            if (completionItem.IsTagHelperAttributeCompletion())
            {
                var descriptionInfo = completionItem.GetAttributeDescriptionInfo();
                _tagHelperDescriptionFactory.TryCreateDescription(descriptionInfo, out markdown);
            }

            if (markdown != null)
            {
                var documentation = new StringOrMarkupContent(
                    new MarkupContent()
                    {
                        Kind = MarkupKind.Markdown,
                        Value = markdown,
                    });
                completionItem.Documentation = documentation;
            }

            return Task.FromResult(completionItem);
        }
    }
}
