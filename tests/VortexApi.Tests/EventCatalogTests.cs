using VortexApi.Domain;

namespace VortexApi.Tests;

public sealed class EventCatalogTests
{
    private static readonly string[] Products = ["HUB", "ERP", "CRM", "WMS", "POS"];

    [Fact]
    public void Cada_tipo_del_catalogo_es_valido_y_no_esta_repetido()
    {
        var types = EventCatalog.Entries.SelectMany(e => Expand(e.Type)).ToList();
        Assert.All(types, t => Assert.True(EventTypePattern.IsValidType(t), t));
        Assert.Equal(types.Count, types.Distinct().Count());
    }

    [Fact]
    public void Solo_el_hub_publica_en_las_categorias_reservadas()
    {
        foreach (var entry in EventCatalog.Entries)
        {
            Assert.All(entry.Producers.Concat(entry.Consumers), p => Assert.Contains(p, Products));
            var reserved = EventTypePattern.ReservedCategories.Contains(EventTypePattern.CategoryOf(Expand(entry.Type).First()));
            Assert.Equal(reserved, entry.Producers.SequenceEqual(["HUB"]));
        }
    }

    [Fact]
    public void Los_eventos_internos_del_crm_solo_los_usa_el_crm()
    {
        var internal_ = EventCatalog.Entries.Where(e => e.Type.StartsWith("crm.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["crm.record.upserted", "crm.record.deleted"], internal_.Select(e => e.Type));
        Assert.All(internal_, e => Assert.Equal(["CRM"], e.Consumers));
    }

    [Fact]
    public void Los_eventos_internos_del_wms_solo_los_usa_el_wms()
    {
        var internal_ = Assert.Single(EventCatalog.Entries, e => e.Type.StartsWith("wms.", StringComparison.Ordinal));
        Assert.Equal("wms.record.upserted", internal_.Type);
        Assert.Equal(["WMS"], internal_.Consumers);
    }

    [Fact]
    public void Los_eventos_internos_del_pos_solo_los_usa_el_pos()
    {
        var internal_ = Assert.Single(EventCatalog.Entries, e => e.Type.StartsWith("pos.", StringComparison.Ordinal));
        Assert.Equal("pos.record.upserted", internal_.Type);
        Assert.Equal(["POS"], internal_.Consumers);
    }

    /// <summary><c>master.item.created|updated|deleted</c> → los tres tipos.</summary>
    private static IEnumerable<string> Expand(string type)
    {
        var lastDot = type.LastIndexOf('.');
        return type[(lastDot + 1)..].Split('|').Select(action => type[..(lastDot + 1)] + action);
    }
}
