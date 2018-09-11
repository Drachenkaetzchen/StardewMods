using System;
using System.Collections.Generic;
using System.Linq;
using ContentPatcher.Framework.Conditions;
using ContentPatcher.Framework.ConfigModels;
using ContentPatcher.Framework.Patches;
using ContentPatcher.Framework.Tokens;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Common.Utilities;
using StardewModdingAPI;

namespace ContentPatcher.Framework
{
    /// <summary>Manages loaded patches.</summary>
    internal class PatchManager : IAssetLoader, IAssetEditor
    {
        /*********
        ** Properties
        *********/
        /****
        ** State
        ****/
        /// <summary>Manages the available contextual tokens.</summary>
        private readonly TokenManager TokenManager;

        /// <summary>Whether to enable verbose logging.</summary>
        private readonly bool Verbose;

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The patches which are permanently disabled for this session.</summary>
        private readonly IList<DisabledPatch> PermanentlyDisabledPatches = new List<DisabledPatch>();

        /// <summary>The patches to apply.</summary>
        private readonly HashSet<IPatch> Patches = new HashSet<IPatch>();

        /// <summary>The patches to apply, indexed by asset name.</summary>
        private InvariantDictionary<HashSet<IPatch>> PatchesByCurrentTarget = new InvariantDictionary<HashSet<IPatch>>();


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="tokenManager">Manages the available contextual tokens.</param>
        /// <param name="verboseLog">Whether to enable verbose logging.</param>
        public PatchManager(IMonitor monitor, TokenManager tokenManager, bool verboseLog)
        {
            this.Monitor = monitor;
            this.TokenManager = tokenManager;
            this.Verbose = verboseLog;
        }

        /****
        ** Patching
        ****/
        /// <summary>Get whether this instance can load the initial version of the given asset.</summary>
        /// <param name="asset">Basic metadata about the asset being loaded.</param>
        public bool CanLoad<T>(IAssetInfo asset)
        {
            IPatch[] patches = this.GetCurrentLoaders(asset).ToArray();
            if (patches.Length > 1)
            {
                this.Monitor.Log($"Multiple patches want to load {asset.AssetName} ({string.Join(", ", from entry in patches orderby entry.LogName select entry.LogName)}). None will be applied.", LogLevel.Error);
                return false;
            }

            bool canLoad = patches.Any();
            this.VerboseLog($"check: [{(canLoad ? "X" : " ")}] can load {asset.AssetName}");
            return canLoad;
        }

        /// <summary>Get whether this instance can edit the given asset.</summary>
        /// <param name="asset">Basic metadata about the asset being loaded.</param>
        public bool CanEdit<T>(IAssetInfo asset)
        {
            bool canEdit = this.GetCurrentEditors(asset).Any();
            this.VerboseLog($"check: [{(canEdit ? "X" : " ")}] can edit {asset.AssetName}");
            return canEdit;
        }

        /// <summary>Load a matched asset.</summary>
        /// <param name="asset">Basic metadata about the asset being loaded.</param>
        public T Load<T>(IAssetInfo asset)
        {
            // get applicable patches for context
            IPatch[] patches = this.GetCurrentLoaders(asset).ToArray();
            if (!patches.Any())
                throw new InvalidOperationException($"Can't load asset key '{asset.AssetName}' because no patches currently apply. This should never happen because it means validation failed.");
            if (patches.Length > 1)
                throw new InvalidOperationException($"Can't load asset key '{asset.AssetName}' because multiple patches apply ({string.Join(", ", from entry in patches orderby entry.LogName select entry.LogName)}). This should never happen because it means validation failed.");

            // apply patch
            IPatch patch = patches.Single();
            if (this.Verbose)
                this.VerboseLog($"Patch \"{patch.LogName}\" loaded {asset.AssetName}.");
            else
                this.Monitor.Log($"{patch.ContentPack.Manifest.Name} loaded {asset.AssetName}.", LogLevel.Trace);

            T data = patch.Load<T>(asset);
            patch.IsApplied = true;
            return data;
        }

        /// <summary>Edit a matched asset.</summary>
        /// <param name="asset">A helper which encapsulates metadata about an asset and enables changes to it.</param>
        public void Edit<T>(IAssetData asset)
        {
            IPatch[] patches = this.GetCurrentEditors(asset).ToArray();
            if (!patches.Any())
                throw new InvalidOperationException($"Can't edit asset key '{asset.AssetName}' because no patches currently apply. This should never happen.");

            InvariantHashSet loggedContentPacks = new InvariantHashSet();
            foreach (IPatch patch in patches)
            {
                if (this.Verbose)
                    this.VerboseLog($"Applied patch \"{patch.LogName}\" to {asset.AssetName}.");
                else if (loggedContentPacks.Add(patch.ContentPack.Manifest.Name))
                    this.Monitor.Log($"{patch.ContentPack.Manifest.Name} edited {asset.AssetName}.", LogLevel.Trace);

                try
                {
                    patch.Edit<T>(asset);
                    patch.IsApplied = true;
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"unhandled exception applying patch: {patch.LogName}.\n{ex}", LogLevel.Error);
                    patch.IsApplied = false;
                }
            }
        }

        /// <summary>Update the current context.</summary>
        /// <param name="contentHelper">The content helper through which to invalidate assets.</param>
        public void UpdateContext(IContentHelper contentHelper)
        {
            this.VerboseLog("Propagating context...");

            // update patches
            InvariantHashSet reloadAssetNames = new InvariantHashSet();
            string prevAssetName = null;
            foreach (IPatch patch in this.Patches.OrderBy(p => p.AssetName).ThenBy(p => p.LogName))
            {
                // log asset name
                if (this.Verbose && prevAssetName != patch.AssetName)
                {
                    this.VerboseLog($"   {patch.AssetName}:");
                    prevAssetName = patch.AssetName;
                }

                // track old values
                string wasAssetName = patch.AssetName;
                bool wasApplied = patch.MatchesContext;

                // update patch
                IContext tokenContext = this.TokenManager.TrackLocalTokens(patch.ContentPack.Pack);
                bool changed = patch.UpdateContext(tokenContext, tokenContext.GetSingleValues(enforceContext: true).ToDictionary(p => p.Name));
                bool shouldApply = patch.MatchesContext && patch.GetTokensUsed().All(p => tokenContext.Contains(p.Name, enforceContext: true));

                // track patches to reload
                bool reload = (wasApplied && changed) || (!wasApplied && shouldApply);
                if (reload)
                {
                    patch.IsApplied = false;
                    if (wasApplied)
                        reloadAssetNames.Add(wasAssetName);
                    if (shouldApply)
                        reloadAssetNames.Add(patch.AssetName);
                }

                // log change
                if (this.Verbose)
                {
                    IList<string> changes = new List<string>();
                    if (wasApplied != shouldApply)
                        changes.Add(shouldApply ? "enabled" : "disabled");
                    if (wasAssetName != patch.AssetName)
                        changes.Add($"target: {wasAssetName} => {patch.AssetName}");
                    string changesStr = string.Join(", ", changes);

                    this.VerboseLog($"      [{(shouldApply ? "X" : " ")}] {patch.LogName}: {(changes.Any() ? changesStr : "OK")}");
                }

                // warn for invalid load patch
                if (patch is LoadPatch loadPatch && patch.MatchesContext && !patch.ContentPack.FileExists(loadPatch.LocalAsset.Value))
                    this.Monitor.Log($"Patch error: {patch.LogName} has a {nameof(PatchConfig.FromFile)} which matches non-existent file '{loadPatch.LocalAsset.Value}'.", LogLevel.Error);
            }

            // rebuild asset name lookup
            this.PatchesByCurrentTarget = new InvariantDictionary<HashSet<IPatch>>(
                from patchGroup in this.Patches.GroupBy(p => p.AssetName, StringComparer.InvariantCultureIgnoreCase)
                let key = patchGroup.Key
                let value = new HashSet<IPatch>(patchGroup)
                select new KeyValuePair<string, HashSet<IPatch>>(key, value)
            );

            // reload assets if needed
            if (reloadAssetNames.Any())
            {
                this.VerboseLog($"   reloading {reloadAssetNames.Count} assets: {string.Join(", ", reloadAssetNames.OrderBy(p => p))}");
                contentHelper.InvalidateCache(asset =>
                {
                    this.VerboseLog($"      [{(reloadAssetNames.Contains(asset.AssetName) ? "X" : " ")}] reload {asset.AssetName}");
                    return reloadAssetNames.Contains(asset.AssetName);
                });
            }
        }

        /****
        ** Patches
        ****/
        /// <summary>Add a patch.</summary>
        /// <param name="patch">The patch to add.</param>
        public void Add(IPatch patch)
        {
            // set initial context
            IContext tokenContext = this.TokenManager.TrackLocalTokens(patch.ContentPack.Pack);
            patch.UpdateContext(tokenContext, tokenContext.GetSingleValues(enforceContext: true).ToDictionary(p => p.Name));

            // add to patch list
            this.VerboseLog($"      added {patch.Type} {patch.AssetName}.");
            this.Patches.Add(patch);

            // add to lookup cache
            if (this.PatchesByCurrentTarget.TryGetValue(patch.AssetName, out HashSet<IPatch> patches))
                patches.Add(patch);
            else
                this.PatchesByCurrentTarget[patch.AssetName] = new HashSet<IPatch> { patch };
        }

        /// <summary>Add a patch that's permanently disabled for this session.</summary>
        /// <param name="patch">The patch to add.</param>
        public void AddPermanentlyDisabled(DisabledPatch patch)
        {
            this.PermanentlyDisabledPatches.Add(patch);
        }

        /// <summary>Get valid patches regardless of context.</summary>
        public IEnumerable<IPatch> GetPatches()
        {
            return this.Patches;
        }

        /// <summary>Get valid patches regardless of context.</summary>
        /// <param name="assetName">The asset name for which to find patches.</param>
        public IEnumerable<IPatch> GetPatches(string assetName)
        {
            if (this.PatchesByCurrentTarget.TryGetValue(assetName, out HashSet<IPatch> patches))
                return patches;
            return new IPatch[0];
        }

        /// <summary>Get patches which are permanently disabled for this session, along with the reason they were.</summary>
        public IEnumerable<DisabledPatch> GetPermanentlyDisabledPatches()
        {
            return this.PermanentlyDisabledPatches;
        }

        /// <summary>Get patches which load the given asset in the current context.</summary>
        /// <param name="asset">The asset being intercepted.</param>
        public IEnumerable<IPatch> GetCurrentLoaders(IAssetInfo asset)
        {
            return this
                .GetPatches(asset.AssetName)
                .Where(patch => patch.Type == PatchType.Load && patch.MatchesContext && patch.IsValidInContext);
        }

        /// <summary>Get patches which edit the given asset in the current context.</summary>
        /// <param name="asset">The asset being intercepted.</param>
        public IEnumerable<IPatch> GetCurrentEditors(IAssetInfo asset)
        {
            PatchType? patchType = this.GetEditType(asset.DataType);
            if (patchType == null)
                return new IPatch[0];

            return this
                .GetPatches(asset.AssetName)
                .Where(patch => patch.Type == patchType && patch.MatchesContext);
        }

        /*********
        ** Private methods
        *********/
        /// <summary>Get the patch type which applies when editing a given asset type.</summary>
        /// <param name="assetType">The asset type.</param>
        private PatchType? GetEditType(Type assetType)
        {
            if (assetType == typeof(Texture2D))
                return PatchType.EditImage;
            if (assetType.IsGenericType && assetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return PatchType.EditData;

            return null;
        }

        /// <summary>Log a message if <see cref="Verbose"/> is enabled.</summary>
        /// <param name="message">The message to log.</param>
        /// <param name="level">The log level.</param>
        private void VerboseLog(string message, LogLevel level = LogLevel.Trace)
        {
            if (this.Verbose)
                this.Monitor.Log(message, level);
        }
    }
}
