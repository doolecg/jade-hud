using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Pug.Properties;
using PugMod;
using PugTilemap;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

public class JadeHudMod : IMod
{
    const float PickInterval = 0.1f;
    const int MaxEntries = 8;

    float nextPick;
    JadeInfo info;
    readonly JadeTooltip tooltip = new JadeTooltip();

    public void EarlyInit() { }

    public void Init() { }

    public void ModObjectLoaded(UnityEngine.Object obj) { }

    public void Shutdown() { tooltip.Dispose(); }

    public void Update()
    {
        if (!AltHeld() || !CanShow())
        {
            info = null;
        }
        else if (Time.unscaledTime >= nextPick)
        {
            nextPick = Time.unscaledTime + PickInterval;
            info = Pick();
        }
        tooltip.Show(info);
    }

    static bool AltHeld()
    {
        return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
    }

    static bool CanShow()
    {
        var ui = Manager.ui;
        return ui != null && ui.mouse != null && ui.currentSelectedUIElement == null && !Manager.menu.IsAnyMenuActive();
    }

    static JadeInfo Pick()
    {
        var world = API.Client.World;
        if (world == null || !world.IsCreated) return null;
        var point = EntityMonoBehaviour.ToWorldFromRender(Manager.ui.mouse.GetMouseGameViewPosition());
        var tile = new int2((int)math.round(point.x), (int)math.round(point.z));
        var em = world.EntityManager;
        // Walls are drawn tall, so the top of a wall sits over the tile behind it; pick the wall below instead.
        var below = tile + new int2(0, -1);
        if (!HasWall(em, tile) && HasWall(em, below)) tile = below;

        var result = new JadeInfo();
        var players = AddEntities(em, tile, result);
        AddTiles(em, tile, result);
        result.Entries.AddRange(players);
        if (result.Entries.Count == 0) return null;
        if (result.Entries.Count > MaxEntries) result.Entries.RemoveRange(MaxEntries, result.Entries.Count - MaxEntries);
        return result;
    }

    // Returns the player entries so the caller can list them after the tiles.
    static List<JadeEntry> AddEntities(EntityManager em, int2 tile, JadeInfo result)
    {
        var players = new List<JadeEntry>();
        using var query = em.CreateEntityQuery(ComponentType.ReadOnly<ObjectDataCD>(), ComponentType.ReadOnly<LocalTransform>());
        using var entities = query.ToEntityArray(Allocator.Temp);

        var found = new List<(float dist, bool isPlayer, Entity entity, float3 pos)>();
        foreach (var e in entities)
        {
            var p = em.GetComponentData<LocalTransform>(e).Position;
            var data = em.GetComponentData<ObjectDataCD>(e);
            if (!CoversTile(p, data, tile)) continue;
            var d = math.distancesq(new float2(p.x, p.z), new float2(tile.x, tile.y));
            found.Add((d, data.objectID == ObjectID.Player, e, p));
        }
        found.Sort((a, b) => a.isPlayer != b.isPlayer ? a.isPlayer.CompareTo(b.isPlayer) : a.dist.CompareTo(b.dist));

        foreach (var f in found)
        {
            var data = em.GetComponentData<ObjectDataCD>(f.entity);
            var objectInfo = PugDatabase.GetObjectInfo(data.objectID, data.variation);
            var entry = new JadeEntry
            {
                Name = ReadName(data),
                Source = ReadSource(data.objectID),
                Icon = objectInfo?.smallIcon != null ? objectInfo.smallIcon : objectInfo?.icon,
            };
            if (em.HasComponent<HealthCD>(f.entity))
            {
                var hp = em.GetComponentData<HealthCD>(f.entity);
                if (hp.maxHealth > 0) { entry.Health = hp.health; entry.MaxHealth = hp.maxHealth; }
            }
            if (result.Entries.Count == 0 && em.HasComponent<GrowingCD>(f.entity) && em.HasComponent<ObjectPropertiesCD>(f.entity))
            {
                entry.IsPlant = true;
                entry.Stage = em.GetComponentData<GrowingCD>(f.entity).currentStage;
                entry.FinalStage = em.GetComponentData<ObjectPropertiesCD>(f.entity).Get<int>(1963487001);
                if (entry.Stage < entry.FinalStage) entry.SecondsToNext = ReadTimer(em, f.entity, f.pos);
            }
            (f.isPlayer ? players : result.Entries).Add(entry);
        }
        return players;
    }

    // An object is on the tile if it rounds to it, or if it is a multi-tile object centred on its position
    // whose footprint covers the tile.
    static bool CoversTile(float3 p, ObjectDataCD data, int2 tile)
    {
        if (math.abs(p.x - tile.x) > 8f || math.abs(p.z - tile.y) > 8f) return false;
        var half = new float2(0.5f, 0.5f);
        var objectInfo = PugDatabase.GetObjectInfo(data.objectID, data.variation);
        if (objectInfo != null && objectInfo.centerIsAtEntityPosition)
            half = math.max(half, new float2(objectInfo.prefabTileSize.x, objectInfo.prefabTileSize.y) * 0.5f);
        return math.abs(p.x - tile.x) < half.x - 0.001f && math.abs(p.z - tile.y) < half.y - 0.001f;
    }

    static void AddTiles(EntityManager em, int2 tile, JadeInfo result)
    {
        using var registryQuery = em.CreateEntityQuery(ComponentType.ReadOnly<SubMapRegistry>());
        if (registryQuery.IsEmpty) return;
        var registry = registryQuery.GetSingleton<SubMapRegistry>();
        var subMapIndex = (tile & -64) >> 6;
        if (!registry.IndexToEntity.TryGetValue(subMapIndex, out var subMap) || !em.HasBuffer<SubMapLayerBuffer>(subMap)) return;

        var layers = em.GetBuffer<SubMapLayerBuffer>(subMap, true);
        var local = tile - subMapIndex * 64;
        for (var i = layers.Length - 1; i >= 0; i--)
        {
            var layer = layers[i].data;
            if (!layer.GetByRef(local) || layer.layer.tileType == TileType.none) continue;
            result.Entries.Add(TileEntry(layer.layer));
        }
    }

    static bool HasWall(EntityManager em, int2 tile)
    {
        using var registryQuery = em.CreateEntityQuery(ComponentType.ReadOnly<SubMapRegistry>());
        if (registryQuery.IsEmpty) return false;
        var registry = registryQuery.GetSingleton<SubMapRegistry>();
        var subMapIndex = (tile & -64) >> 6;
        if (!registry.IndexToEntity.TryGetValue(subMapIndex, out var subMap) || !em.HasBuffer<SubMapLayerBuffer>(subMap)) return false;

        var layers = em.GetBuffer<SubMapLayerBuffer>(subMap, true);
        var local = tile - subMapIndex * 64;
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i].data;
            if (layer.GetByRef(local) && layer.layer.tileType == TileType.wall) return true;
        }
        return false;
    }

    static JadeEntry TileEntry(TileCD tile)
    {
        var itemInfo = PugDatabase.TryGetTileItemInfo(tile.tileType, tile.tileset);
        var entry = new JadeEntry();
        if (itemInfo != null)
        {
            entry.Name = ReadName(new ObjectDataCD { objectID = itemInfo.objectID, amount = 1 });
            entry.Icon = itemInfo.smallIcon != null ? itemInfo.smallIcon : itemInfo.icon;
        }
        else
        {
            var tileset = Enum.IsDefined(typeof(Tileset), tile.tileset) ? ((Tileset)tile.tileset).ToString() : "Tile";
            entry.Name = Spaced(tileset) + " " + Spaced(tile.tileType.ToString());
        }
        entry.Source = Enum.IsDefined(typeof(Tileset), tile.tileset) ? "Core Keeper" : "Modded";
        return entry;
    }

    static string Spaced(string s)
    {
        return Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");
    }

    static string ReadName(ObjectDataCD data)
    {
        try
        {
            var name = PlayerController.GetObjectName(new ContainedObjectsBuffer { objectData = data }, true);
            if (name != null && !string.IsNullOrEmpty(name.text)) return name.text;
        }
        catch (Exception) { }
        return Spaced(data.objectID.ToString());
    }

    static string ReadSource(ObjectID id)
    {
        if (Enum.IsDefined(typeof(ObjectID), id)) return "Core Keeper";
        var idName = id.ToString();
        foreach (var mod in API.ModLoader.LoadedMods)
        {
            if (mod.Assets == null) continue;
            foreach (var asset in mod.Assets)
                if (asset != null && asset.name == idName) return mod.Metadata.name;
        }
        return "Modded";
    }

    // The timer lives on a separate entity. Clients may not have it, so in a hosted
    // world fall back to the server world's copy of the same plant.
    static float ReadTimer(EntityManager clientEm, Entity plant, float3 pos)
    {
        if (TryTimer(clientEm, plant, out var t)) return t;

        var server = API.Server.World;
        if (server == null || !server.IsCreated) return -1f;
        var sem = server.EntityManager;
        using var query = sem.CreateEntityQuery(ComponentType.ReadOnly<GrowTimerRefCD>(), ComponentType.ReadOnly<LocalTransform>());
        using var entities = query.ToEntityArray(Allocator.Temp);
        foreach (var e in entities)
        {
            var p = sem.GetComponentData<LocalTransform>(e).Position;
            if (math.distancesq(p, pos) < 0.01f && TryTimer(sem, e, out t)) return t;
        }
        return -1f;
    }

    static bool TryTimer(EntityManager em, Entity plant, out float seconds)
    {
        seconds = -1f;
        if (!em.Exists(plant) || !em.HasComponent<GrowTimerRefCD>(plant)) return false;
        var timerEntity = em.GetComponentData<GrowTimerRefCD>(plant).GrowTimerEntity;
        if (!em.Exists(timerEntity) || !em.HasComponent<GrowTimerCD>(timerEntity)) return false;
        var timer = em.GetComponentData<GrowTimerCD>(timerEntity);
        seconds = math.max(0f, timer.StageTime - timer.Value);
        return true;
    }
}

public class JadeEntry
{
    public string Name;
    public string Source;
    public Sprite Icon;
    public bool IsPlant;
    public int Stage;
    public int FinalStage;
    public float SecondsToNext;
    public int Health;
    public int MaxHealth;
}

public class JadeInfo
{
    public readonly List<JadeEntry> Entries = new List<JadeEntry>();

    public string Key()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in Entries)
            sb.Append(e.Name).Append('|').Append(e.Source).Append('|').Append(e.Stage).Append('|').Append(Mathf.CeilToInt(e.SecondsToNext)).Append('|').Append(e.Health).Append('/').Append(e.MaxHealth).Append(';');
        return sb.ToString();
    }
}

// Draws the hover box by cloning the game's own item tooltip, so font, frame and colours match.
public class JadeTooltip
{
    static readonly Color SourceColor = new Color(0.45f, 0.65f, 1f);
    static readonly Color LineColor = new Color(0.85f, 0.8f, 0.7f);

    SlotHoverTooltip tip;
    string shownKey;

    public void Dispose()
    {
        if (tip != null) UnityEngine.Object.Destroy(tip.gameObject);
        tip = null;
    }

    public void Show(JadeInfo info)
    {
        if (info == null)
        {
            if (tip != null) tip.gameObject.SetActive(false);
            shownKey = null;
            return;
        }
        if (!Ensure()) return;

        tip.gameObject.SetActive(true);
        var key = info.Key();
        if (key == shownKey) return;
        shownKey = key;
        Render(info);
    }

    bool Ensure()
    {
        if (tip != null) return true;
        var source = Manager.ui != null && Manager.ui.mouse != null ? Manager.ui.mouse.slotHoverTooltip : null;
        if (source == null) return false;
        tip = UnityEngine.Object.Instantiate(source, source.transform.parent);
        tip.name = "JadeHudTooltip";
        return true;
    }

    void Render(JadeInfo info)
    {
        // the clone keeps whatever text the game's tooltip showed when it was copied, so hide it all first
        foreach (var text in tip.GetComponentsInChildren<PugText>(true))
            text.gameObject.SetActive(false);
        tip.statsContainer.SetActive(false);
        tip.setBonusesContainer.SetActive(false);
        tip.hoverMaterialsContainer.SetActive(false);
        tip.hoverFiltersContainer.SetActive(false);
        tip.tagsContainer.SetActive(false);
        tip.coinTextContainer.SetActive(false);
        tip.durabilityText.gameObject.SetActive(false);
        tip.levelText.gameObject.SetActive(false);
        tip.compareText.gameObject.SetActive(false);
        tip.topLabelText.gameObject.SetActive(false);
        tip.topLabelTextBackground.gameObject.SetActive(false);
        tip.topLabelTextBlackBackground.gameObject.SetActive(false);
        tip.hoverTitleIcon.enabled = false;

        var head = info.Entries[0];
        var icon = head.Icon;
        var iconSize = 2f;
        var iconWidth = 0f;
        var textX = 0.25f;
        if (icon != null)
        {
            var size = icon.bounds.size;
            var maxSide = Mathf.Max(size.x, size.y, 0.0625f);
            var scale = maxSide >= iconSize ? iconSize / maxSide : Mathf.Floor(iconSize / maxSide);
            tip.hoverTitleIcon.enabled = true;
            tip.hoverTitleIcon.sprite = icon;
            tip.hoverTitleIcon.color = Color.white;
            tip.hoverTitleIcon.transform.localScale = new Vector3(scale, scale, 1f);
            iconWidth = iconSize + 0.25f;
            textX += iconWidth;
        }

        // title and source always sit side by side with the icon; extra lines flow underneath
        var cursor = new Vector3(textX, -0.25f, 0f);
        tip.hoverTitle.gameObject.SetActive(true);
        tip.hoverTitle.localize = false;
        tip.hoverTitle.formatFields = new string[0];
        tip.hoverTitle.Render(head.Name);
        tip.hoverTitle.SetTempColor(Color.white);
        cursor = UIMouse.UpdatePositionOfHoverText(tip.hoverTitle, cursor);
        var widest = tip.hoverTitle.dimensions.width + iconWidth;

        var headerLines = new List<Line> { new Line(head.Source, SourceColor) };
        var extraLines = new List<Line>();
        if (head.MaxHealth > 0)
            extraLines.Add(new Line("Health: " + head.Health + "/" + head.MaxHealth, LineColor));
        if (head.IsPlant)
        {
            extraLines.Add(new Line("Growth: stage " + (head.Stage + 1) + "/" + (head.FinalStage + 1), LineColor));
            if (head.Stage < head.FinalStage)
                extraLines.Add(new Line(head.SecondsToNext >= 0f ? "Next stage: " + Format(head.SecondsToNext) : "Next stage: unknown", LineColor));
        }
        for (var i = 1; i < info.Entries.Count; i++)
        {
            var e = info.Entries[i];
            extraLines.Add(new Line("- " + e.Name + (e.Source == "Core Keeper" ? "" : " (" + e.Source + ")"), Color.white));
        }

        tip.descriptionsContainer.SetActive(true);
        foreach (var d in tip.hoverDescriptions) d.gameObject.SetActive(false);
        var slot = 0;
        foreach (var line in headerLines)
            cursor = PlaceLine(slot++, line, cursor, iconWidth, ref widest);
        if (icon != null && cursor.y > -iconSize - 0.25f) cursor.y = -iconSize - 0.25f;
        cursor.x = 0.25f;
        foreach (var line in extraLines)
            cursor = PlaceLine(slot++, line, cursor, 0f, ref widest);

        var width = widest + 0.5625f;
        if (width % 0.125f != 0f) width += 0.0625f;
        var height = Mathf.Abs(cursor.y) + 0.125f;
        if (height % 0.125f != 0f) height += 0.0625f;

        tip.hoverTopLeft.transform.localPosition = new Vector3(0.5f, -0.5f, 0f);
        var halfW = width / 2f;
        var halfH = height / 2f;
        var snapW = halfW % 0.0625f > 0f ? 0.0625f - halfW % 0.0625f : 0f;
        var snapH = halfH % 0.0625f > 0f ? 0.0625f - halfH % 0.0625f : 0f;
        tip.hoverTextBackground.gameObject.SetActive(true);
        tip.hoverTextBackground.transform.localPosition = new Vector2(halfW + snapW, -(halfH + snapH));
        tip.hoverTextBackground.size = new Vector2(width, height);
        tip.hoverTextBlackBackground.size = tip.hoverTextBackground.size;
        if (icon != null)
            tip.hoverTitleIcon.transform.localPosition = new Vector3(-width / 2f + 0.25f + iconSize / 2f, height / 2f - 0.25f - iconSize / 2f, 0f);
    }

    Vector3 PlaceLine(int slot, Line line, Vector3 cursor, float indent, ref float widest)
    {
        if (tip.hoverDescriptions.Count <= slot)
            tip.hoverDescriptions.Add(UnityEngine.Object.Instantiate(tip.descriptionTextPrefab, tip.descriptionsContainer.transform));
        var text = tip.hoverDescriptions[slot];
        text.gameObject.SetActive(true);
        text.maxWidth = 30f;
        text.localize = false;
        text.localizePlaceholders = false;
        text.formatFields = new string[0];
        text.Render(line.Text);
        text.SetTempColor(line.Color);
        cursor = UIMouse.UpdatePositionOfHoverText(text, cursor);
        if (text.dimensions.width + indent > widest) widest = text.dimensions.width + indent;
        return cursor;
    }

    struct Line
    {
        public string Text;
        public Color Color;
        public Line(string text, Color color) { Text = text; Color = color; }
    }

    static string Format(float s)
    {
        var t = (int)Mathf.Ceil(s);
        return t >= 60 ? (t / 60) + "m " + (t % 60).ToString("00") + "s" : t + "s";
    }
}
