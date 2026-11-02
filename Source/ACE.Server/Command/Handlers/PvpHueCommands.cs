using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// PROTOTYPE probe: /pvphue list | &lt;palette&gt; [shade] | reset. Recolours a player's worn items live by changing each
    /// item's PaletteTemplate and Shade, to test whether team-coloured PvP template armor can be done by recolouring
    /// instead of building separate Blue template characters.
    ///
    /// The target is the admin's selected player, else the admin. Changes are REAL property writes on the items, so they
    /// persist on save; `reset` is the undo (originals are kept in memory only, so a server restart forgets them).
    /// Palette ids are validated per item against its ClothingBase ClothingSubPalEffects, because an unknown id would
    /// silently render as the first entry. The testable rules live in PvpHueProbe.
    /// </summary>
    public static class PvpHueCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        internal const string Usage = "[PvpHue] Usage: /pvphue <list | reset | audit | suit <templateKey> <red|blue|neutral|own|palette [shade]> | <palette> [shade 0-1] | look <sourceWcid> [slot,slot...]>";

        private static readonly HueMemory Memory = new HueMemory();

        private static bool warned;

        [CommandHandler("pvphue", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "PROTOTYPE: recolour a player's worn items live by palette template and shade.",
            "list              - each worn item's ClothingBase, palette, shade and valid palette ids, plus the ids valid for all\n" +
            "<palette> [shade] - recolour every worn item that supports the palette (shade 0-1, default keeps each item's own)\n" +
            "look <wcid> [slot,slot...] - copy a retail weenie's LOOK (ClothingBase, palette, shade, icon) onto worn items; slots: helm chest pauldrons bracers gauntlets girth tassets greaves sollerets; default = every worn item the source covers\n" +
            "audit             - for every offered PvP template and every worn kit piece: what the Red, Blue and Neutral team hue resolves to (palette/shade and override or global, or skipped and why), from the live settings\n" +
            "suit <templateKey> <red|blue|neutral|own> - copy each kit piece of that template onto the worn piece in the same slot, dressed in that team hue (own = the template's own look); the exact look a match would issue. Undo with reset\n" +
            "suit <templateKey> <palette> [shade 0-1] - the same, dressed as one Tugak Brawl player of that palette would be (for example: suit light 13 0.2)\n" +
            "reset             - restore the remembered original look of every item the target possesses (worn or carried)\n" +
            "Target: your selected player, else yourself. Writes are real item properties (they persist on save); reset undoes them.")]
        public static void HandlePvpHue(Session session, params string[] parameters)
        {
            var admin = session.Player;

            var isLook = parameters != null && parameters.Length >= 1 && parameters[0].Equals("look", StringComparison.OrdinalIgnoreCase);
            HueCommand cmd = default;

            if (!isLook && !PvpHueProbe.TryParseArgs(parameters, out cmd))
            {
                Reply(session, Usage);
                return;
            }

            if (!isLook && cmd.Kind == HueCommandKind.Audit)
            {
                HandleAudit(session);
                return;
            }

            var target = ResolveTarget(admin);

            if (target == null)
            {
                Reply(session, "[PvpHue] Selected object is not a player; clear the selection to target yourself.");
                return;
            }

            if (isLook)
            {
                HandleLook(session, admin, target, parameters);
                return;
            }

            switch (cmd.Kind)
            {
                case HueCommandKind.List:
                    HandleList(session, target);
                    break;
                case HueCommandKind.Reset:
                    HandleReset(session, admin, target);
                    break;
                case HueCommandKind.Suit:
                    HandleSuit(session, admin, target, cmd);
                    break;
                default:
                    HandleApply(session, admin, target, cmd.Palette, cmd.Shade);
                    break;
            }
        }

        /// <summary>The selected player, the admin when nothing is selected, or null when the selection is not a player.</summary>
        private static Player ResolveTarget(Player admin)
        {
            if (!admin.CurrentAppraisalTarget.HasValue)
                return admin;

            var selected = admin.CurrentLandblock?.GetObject(new ObjectGuid(admin.CurrentAppraisalTarget.Value));
            return selected as Player;
        }

        private static bool HasOwnPaletteRows(WorldObject item) =>
            item.Biota.PropertiesPalette.GetCount(item.BiotaDatabaseLock) > 0
            || item.Biota.PropertiesTextureMap.GetCount(item.BiotaDatabaseLock) > 0
            || item.Biota.PropertiesAnimPart.GetCount(item.BiotaDatabaseLock) > 0;

        private static List<(WorldObject Item, HueItem View)> Snapshot(Player target)
        {
            var result = new List<(WorldObject, HueItem)>();

            foreach (var item in target.EquippedObjects.Values.OrderBy(i => i.Name))
            {
                var cb = item.GetProperty(PropertyDataId.ClothingBase);
                IReadOnlyCollection<uint> valid = null;

                if (cb.HasValue && cb.Value != 0)
                {
                    var table = DatManager.PortalDat.ReadFromDat<ClothingTable>(cb.Value);
                    valid = table?.ClothingSubPalEffects.Keys.ToList();
                }

                result.Add((item, new HueItem(item.Guid.Full, item.Name, cb, valid)));
            }

            return result;
        }

        private static void HandleList(Session session, Player target)
        {
            var snap = Snapshot(target);
            Reply(session, $"[PvpHue] {target.Name}: {snap.Count} worn item(s).");

            foreach (var (item, view) in snap)
            {
                var cb = view.ClothingBase.HasValue ? $"0x{view.ClothingBase.Value:X8}" : "none";
                var ids = string.Join(",", view.ValidPalettes.OrderBy(p => p));
                Reply(session, $"  {item.Name} wcid={item.WeenieClassId} cb={cb} palette={item.GetProperty(PropertyInt.PaletteTemplate)?.ToString() ?? "-"} " +
                    $"shade={item.GetProperty(PropertyFloat.Shade)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-"} valid=[{ids}]" +
                    (HasOwnPaletteRows(item) ? " [own palette rows - may not recolour visibly]" : ""));
            }

            var views = snap.Select(s => s.View).ToList();
            var common = PvpHueProbe.CommonPalettes(views);
            var skipped = PvpHueProbe.Unrecolourable(views);

            Reply(session, $"[PvpHue] Valid for every recolourable item ({common.Count}): [{string.Join(",", common)}]");
            Reply(session, $"[PvpHue] Not recolourable ({skipped.Count}): {(skipped.Count == 0 ? "none" : string.Join(", ", skipped.Select(s => s.Name)))}");
        }

        private static void HandleApply(Session session, Player admin, Player target, int palette, double? shade)
        {
            var snap = Snapshot(target);
            int changed = 0;
            var skipped = new List<string>();

            foreach (var (item, view) in snap)
            {
                var reason = PvpHueProbe.Classify(view, palette);

                if (reason != HueSkipReason.None)
                {
                    skipped.Add($"{item.Name} ({Describe(reason)})");
                    continue;
                }

                var oldPalette = item.GetProperty(PropertyInt.PaletteTemplate);
                var oldShade = item.GetProperty(PropertyFloat.Shade);

                Memory.Remember(item.Guid.Full, Capture(item));

                var useShade = shade ?? oldShade ?? 0.0;

                // One path for every item (Clothing.SetProperties is not used): palette and shade always, the icon only
                // when the table has one for this palette and the item does not opt out of clothing icons.
                var icon = DatManager.PortalDat.ReadFromDat<ClothingTable>(view.ClothingBase.Value).GetIcon((uint)palette);

                if (PvpHueProbe.ShouldWriteIcon(icon, item.GetProperty(PropertyBool.IgnoreCloIcons)))
                    item.SetProperty(PropertyDataId.Icon, icon);

                item.SetProperty(PropertyInt.PaletteTemplate, palette);
                item.SetProperty(PropertyFloat.Shade, useShade);

                log.Info($"[PvpHue] {admin.Name} -> {target.Name}: {item.Name} 0x{item.Guid.Full:X8} palette {Show(oldPalette)} -> {palette}, shade {Show(oldShade)} -> {useShade.ToString("0.###", CultureInfo.InvariantCulture)}");

                Broadcast(target, item);
                changed++;
            }

            if (changed > 0)
                target.EnqueueBroadcast(new GameMessageObjDescEvent(target));

            // reply is built below; this flag only drives the one-time persistence warning
            var warn = changed > 0 && !warned;
            if (warn)
                warned = true;

            log.Info($"[PvpHue] {admin.Name} recoloured {target.Name}: palette={palette} shade={(shade.HasValue ? shade.Value.ToString("0.###", CultureInfo.InvariantCulture) : "keep")} items={changed} skipped={skipped.Count}");

            Reply(session, $"[PvpHue] Recoloured {changed} item(s) on {target.Name} to palette {palette}.");

            if (skipped.Count > 0)
                Reply(session, $"[PvpHue] Skipped {skipped.Count}: {string.Join("; ", skipped)}");

            if (warn)
                Reply(session, "[PvpHue] Warning: changes persist until /pvphue reset and the originals are lost from memory on server restart.");
        }

        /// <summary>Every look-bearing property of an item, so reset can restore it whichever command changed it first.</summary>
        private static HueOriginal Capture(WorldObject item) => new HueOriginal(
            item.GetProperty(PropertyDataId.Icon),
            item.GetProperty(PropertyInt.PaletteTemplate),
            item.GetProperty(PropertyFloat.Shade),
            item.GetProperty(PropertyDataId.Setup),
            item.GetProperty(PropertyDataId.ClothingBase),
            item.GetProperty(PropertyDataId.PaletteBase),
            item.GetProperty(PropertyFloat.Shade2),
            item.GetProperty(PropertyFloat.Shade3),
            item.GetProperty(PropertyFloat.Shade4),
            item.GetProperty(PropertyBool.IgnoreCloIcons));

        private static void HandleLook(Session session, Player admin, Player target, string[] parameters)
        {
            if (parameters.Length < 2 || parameters.Length > 3
                || !uint.TryParse(parameters[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wcid))
            {
                Reply(session, Usage);
                return;
            }

            List<int> masks = null;

            if (parameters.Length == 3 && !PvpHueProbe.TryParseSlots(parameters[2], out masks, out var bad))
            {
                Reply(session, $"[PvpHue] Unknown slot '{bad}'. Slots: helm, chest, pauldrons, bracers, gauntlets, girth, tassets, greaves, sollerets.");
                return;
            }

            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie == null)
            {
                Reply(session, $"[PvpHue] No world weenie with wcid {wcid}.");
                return;
            }

            // The same extraction and validation the team hue "@wcid" swap uses (PvpWorldLooks.FromWeenie, PvpLookSource.Problem).
            var lookSource = PvpWorldLooks.FromWeenie(wcid, weenie);
            var src = lookSource.Look;
            var srcName = lookSource.Name;

            var problem = lookSource.Problem();

            if (problem != null)
            {
                Reply(session, $"[PvpHue] Cannot use {srcName} ({wcid}) as a look source: {problem}.");
                return;
            }

            var tableIcon = DatManager.PortalDat.ReadFromDat<ClothingTable>(src.ClothingBase.Value)?.GetIcon((uint)(src.PaletteTemplate ?? 0)) ?? 0;
            var plan = PvpHueProbe.PlanLook(src, tableIcon);

            var worn = target.EquippedObjects.Values.OrderBy(i => i.Name)
                .Select(i => new HueWorn(i.Guid.Full, i.Name, (int?)i.CurrentWieldedLocation, HasOwnPaletteRows(i))).ToList();
            var selection = PvpHueProbe.SelectLookTargets(worn, lookSource.ValidLocations, masks);
            var byGuid = target.EquippedObjects.Values.ToDictionary(i => i.Guid.Full);

            foreach (var guid in selection.Chosen)
                Reply(session, ApplyLookPlan(admin, target, byGuid[guid], plan, $"{srcName} ({wcid})"));

            if (selection.Chosen.Count > 0)
                target.EnqueueBroadcast(new GameMessageObjDescEvent(target));

            Reply(session, $"[PvpHue] Tailored {selection.Chosen.Count} item(s) on {target.Name} to look like {srcName} ({wcid}).");

            if (selection.Skipped.Count > 0)
                Reply(session, "[PvpHue] Skipped: " + string.Join("; ", selection.Skipped.Select(s => $"{byGuid[s.Key].Name} ({s.Value})")));

            if (selection.Chosen.Count > 0 && !warned)
            {
                warned = true;
                Reply(session, "[PvpHue] Warning: changes persist until /pvphue reset and the originals are lost from memory on server restart.");
            }
        }

        /// <summary>
        /// Writes one look plan onto a worn item: remembers the original for reset, copies the planned fields (a field the plan lacks is
        /// removed), logs, and sends the update. Returns the admin reply line. Shared by /pvphue look and /pvphue suit.
        /// </summary>
        private static string ApplyLookPlan(Player admin, Player target, WorldObject item, LookPlan plan, string sourceText)
        {
            Memory.Remember(item.Guid.Full, Capture(item));

            var oldCb = item.GetProperty(PropertyDataId.ClothingBase);
            var oldPalette = item.GetProperty(PropertyInt.PaletteTemplate);
            var oldShade = item.GetProperty(PropertyFloat.Shade);

            if (!plan.KeepSetup) Restore(item, PropertyDataId.Setup, plan.Target.Setup);
            Restore(item, PropertyDataId.ClothingBase, plan.Target.ClothingBase);
            Restore(item, PropertyDataId.PaletteBase, plan.Target.PaletteBase);
            Restore(item, PropertyInt.PaletteTemplate, plan.Target.PaletteTemplate);
            Restore(item, PropertyFloat.Shade, plan.Target.Shade);
            Restore(item, PropertyFloat.Shade2, plan.Target.Shade2);
            Restore(item, PropertyFloat.Shade3, plan.Target.Shade3);
            Restore(item, PropertyFloat.Shade4, plan.Target.Shade4);
            Restore(item, PropertyBool.IgnoreCloIcons, plan.Target.IgnoreCloIcons);
            if (!plan.KeepIcon) Restore(item, PropertyDataId.Icon, plan.Target.Icon);

            var slot = PvpHueProbe.SlotName((int)item.CurrentWieldedLocation) ?? "?";

            log.Info($"[PvpHue] {admin.Name} -> {target.Name}: look {item.Name} 0x{item.Guid.Full:X8} ({slot}) from {sourceText}: ClothingBase {Show(oldCb)} -> {Show(plan.Target.ClothingBase)}, palette {Show(oldPalette)} -> {Show(plan.Target.PaletteTemplate)}, shade {Show(oldShade)} -> {Show(plan.Target.Shade)}");

            Broadcast(target, item);

            return $"[PvpHue] {slot}: {item.Name} <- {sourceText} ClothingBase 0x{plan.Target.ClothingBase:X8} palette {Show(plan.Target.PaletteTemplate)} shade {Show(plan.Target.Shade)}";
        }

        /// <summary>The live team hue settings with the master switch forced ON, so the audit and the preview show what a match WOULD issue.</summary>
        private static PvpTeamHueDials PreviewDials(out bool switchedOn)
        {
            var dials = PvpTeamHueSettings.Snapshot();
            switchedOn = dials.Enabled;

            return dials with { Enabled = true };
        }

        private static void HandleAudit(Session session)
        {
            if (!PvpMatchManager.TemplatesLoaded)
            {
                Reply(session, "[PvpHue] The template catalog is not loaded yet.");
                return;
            }

            var dials = PreviewDials(out var switchedOn);
            var definitions = PvpMatchManager.OfferedTemplateDefinitions();

            Reply(session, $"[PvpHue] Team hue audit of {definitions.Count} offered template(s). The master switch pvp_team_hue_enabled is {(switchedOn ? "ON" : "OFF (matches issue no team hue; this shows what they would)")}. " +
                $"Global: red {Look(dials.Red)}, blue {Look(dials.Blue)}, neutral {Look(dials.Neutral)}; {dials.Overrides.Count} override(s). " +
                $"Tugak pool (shade {dials.FfaShade.ToString("0.###", CultureInfo.InvariantCulture)}): {(dials.FfaPalettes != null && dials.FfaPalettes.Count > 0 ? string.Join(",", dials.FfaPalettes) : "empty, so Tugak falls back to one coin-flipped Red or Blue")}.");

            foreach (var definition in definitions)
            {
                Reply(session, $"[PvpHue] {definition.Key} v{definition.Version} ({PvpMatchManager.TemplateLabel(definition.Key)}):");

                var lines = PvpTeamHue.AuditLines(definition, dials, PvpClothingPalettes.Palettes, PvpWorldLooks.Get);

                if (lines.Count == 0)
                    Reply(session, "  (no worn kit piece)");

                foreach (var line in lines)
                    Reply(session, line);
            }
        }

        private static string Look(PvpTeamHueLook look) => look.Palette == 0 ? "keep own" : $"{look.Palette}/{look.Shade.ToString("0.###", CultureInfo.InvariantCulture)}";

        private static void HandleSuit(Session session, Player admin, Player target, HueCommand cmd)
        {
            if (!PvpMatchManager.TemplatesLoaded)
            {
                Reply(session, "[PvpHue] The template catalog is not loaded yet.");
                return;
            }

            var definition = PvpMatchManager.TemplateDefinition(cmd.TemplateKey);

            if (definition == null)
            {
                Reply(session, $"[PvpHue] There is no parsed template {cmd.TemplateKey}.");
                return;
            }

            var dials = PreviewDials(out _);

            // A numeric palette previews one Tugak seat: the same FfaSeatDials + Apply a Tugak dispatch runs, with no look swaps.
            var applied = cmd.NumericPalette
                ? PvpTeamHue.Apply(definition, KitHue.Neutral, PvpTeamHue.FfaSeatDials(dials, cmd.Palette, cmd.Shade ?? 0.0), PvpClothingPalettes.Palettes, PvpClothingPalettes.Icon)
                : cmd.Hue.HasValue
                    ? PvpTeamHue.Apply(definition, cmd.Hue.Value, dials, PvpClothingPalettes.Palettes, PvpClothingPalettes.Icon, PvpWorldLooks.Get)
                    : new HueApplyResult { Definition = definition };

            var worn = target.EquippedObjects.Values.OrderBy(i => i.Name)
                .Select(i => new HueWorn(i.Guid.Full, i.Name, (int?)i.CurrentWieldedLocation, HasOwnPaletteRows(i))).ToList();
            var plan = PvpTeamHue.PlanSuit(applied.Definition, worn);
            var byGuid = target.EquippedObjects.Values.ToDictionary(i => i.Guid.Full);
            var hueText = cmd.NumericPalette
                ? $"Tugak palette {cmd.Palette}/{(cmd.Shade ?? 0.0).ToString("0.###", CultureInfo.InvariantCulture)}"
                : cmd.Hue.HasValue ? PvpTeamHue.Token(cmd.Hue.Value) : "own look";

            foreach (var step in plan.Steps)
            {
                var source = step.Source;
                var tableIcon = source.ClothingBase.HasValue ? PvpClothingPalettes.Icon(source.ClothingBase.Value, (uint)(source.PaletteTemplate ?? 0)) : 0;

                Reply(session, ApplyLookPlan(admin, target, byGuid[step.TargetGuid], PvpHueProbe.PlanLook(source, tableIcon), $"{definition.Key} {PvpTeamHue.ItemName(step.Kit)} ({hueText})"));
            }

            if (plan.Steps.Count > 0)
                target.EnqueueBroadcast(new GameMessageObjDescEvent(target));

            Reply(session, $"[PvpHue] Dressed {plan.Steps.Count} worn piece(s) on {target.Name} as {definition.Key} in {hueText}. Undo with /pvphue reset.");

            if (applied.Skipped.Count > 0)
                Reply(session, "[PvpHue] Kept their own look: " + string.Join("; ", applied.Skipped.Select(s => $"{s.Name} ({s.Detail})")));

            if (plan.Left.Count > 0)
                Reply(session, "[PvpHue] Not matched: " + string.Join("; ", plan.Left));

            if (plan.Steps.Count > 0 && !warned)
            {
                warned = true;
                Reply(session, "[PvpHue] Warning: changes persist until /pvphue reset and the originals are lost from memory on server restart.");
            }
        }

        private static string Show<T>(T? v) where T : struct => v.HasValue ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : "-";

        private static void HandleReset(Session session, Player admin, Player target)
        {
            int restored = 0;
            bool anyWielded = false;

            // Every possession, not just worn items: an item recoloured while worn and unequipped since is still restored.
            foreach (var item in target.GetAllPossessions())
            {
                if (!Memory.TryGet(item.Guid.Full, out var o))
                    continue;

                var oldPalette = item.GetProperty(PropertyInt.PaletteTemplate);
                var oldShade = item.GetProperty(PropertyFloat.Shade);

                Restore(item, PropertyDataId.Icon, o.Icon);
                Restore(item, PropertyDataId.Setup, o.Setup);
                Restore(item, PropertyDataId.ClothingBase, o.ClothingBase);
                Restore(item, PropertyDataId.PaletteBase, o.PaletteBase);
                Restore(item, PropertyInt.PaletteTemplate, o.PaletteTemplate);
                Restore(item, PropertyFloat.Shade, o.Shade);
                Restore(item, PropertyFloat.Shade2, o.Shade2);
                Restore(item, PropertyFloat.Shade3, o.Shade3);
                Restore(item, PropertyFloat.Shade4, o.Shade4);
                Restore(item, PropertyBool.IgnoreCloIcons, o.IgnoreCloIcons);

                Memory.Forget(item.Guid.Full);
                log.Info($"[PvpHue] {admin.Name} -> {target.Name}: reset {item.Name} 0x{item.Guid.Full:X8} palette {Show(oldPalette)} -> {Show(o.PaletteTemplate)}, shade {Show(oldShade)} -> {Show(o.Shade)}");

                target.EnqueueBroadcast(new GameMessageUpdateObject(item));

                if (item.CurrentWieldedLocation != null)
                {
                    anyWielded = true;
                    VisualEffectManager.Forget(target.Session, item);
                }

                restored++;
            }

            if (anyWielded)
                target.EnqueueBroadcast(new GameMessageObjDescEvent(target));

            Reply(session, $"[PvpHue] Restored {restored} item(s) on {target.Name}. ({Memory.Count} remembered item(s) not held by this player remain.)");
        }

        /// <summary>
        /// Mirrors RecipeManager.UpdateObj for a wielded item: UpdateObject for the item, ObjDesc for the wearer (sent by
        /// the caller once), and the once-only aura send guard reopened (not a resend; the aura returns on the next
        /// genuine rebuild). The guard only matters for armor that carries a visual-effect aura.
        /// </summary>
        private static void Broadcast(Player target, WorldObject item)
        {
            target.EnqueueBroadcast(new GameMessageUpdateObject(item));
            VisualEffectManager.Forget(target.Session, item);
        }

        private static void Restore(WorldObject item, PropertyDataId prop, uint? v)
        {
            if (PvpHueProbe.RestoreRemoves(v)) item.RemoveProperty(prop); else item.SetProperty(prop, v.Value);
        }

        private static void Restore(WorldObject item, PropertyBool prop, bool? v)
        {
            if (PvpHueProbe.RestoreRemoves(v)) item.RemoveProperty(prop); else item.SetProperty(prop, v.Value);
        }

        private static void Restore(WorldObject item, PropertyInt prop, int? v)
        {
            if (PvpHueProbe.RestoreRemoves(v)) item.RemoveProperty(prop); else item.SetProperty(prop, v.Value);
        }

        private static void Restore(WorldObject item, PropertyFloat prop, double? v)
        {
            if (PvpHueProbe.RestoreRemoves(v)) item.RemoveProperty(prop); else item.SetProperty(prop, v.Value);
        }

        private static string Describe(HueSkipReason r) => r switch
        {
            HueSkipReason.NoClothingBase => "no ClothingBase",
            HueSkipReason.NoSubPalettes => "no sub-palettes",
            _ => "palette not supported",
        };

        private static void Reply(Session session, string text) =>
            CommandHandlerHelper.WriteOutputInfo(session, text, ChatMessageType.Broadcast);
    }
}
