using System.Globalization;
using System.Net;
using System.Text;
using VortexApi.Contracts;
using VortexApi.Domain;

namespace VortexApi.Services;

/// <summary>
/// Página del enlace público: el documento (emisor, cliente, líneas, totales, condiciones) y, si sigue abierto, el
/// formulario para aceptarlo con nombre y firma dibujada o rechazarlo. HTML autocontenido (sin recursos externos), todo
/// el contenido del documento pasa por <see cref="WebUtility.HtmlEncode"/>.
/// </summary>
public static class PublicLinkPage
{
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = new()
    {
        ["es"] = new()
        {
            ["number"] = "N.º", ["issued"] = "Emitida", ["valid"] = "Válida hasta", ["customer"] = "Cliente", ["seller"] = "Atiende",
            ["description"] = "Descripción", ["qty"] = "Cant.", ["unit_price"] = "P. unit.", ["amount"] = "Importe",
            ["optional"] = "Opcionales (no suman al total)", ["notes"] = "Condiciones", ["respond"] = "Tu respuesta",
            ["name"] = "Nombre completo", ["email"] = "Correo (opcional)", ["comment"] = "Comentario (opcional)",
            ["sign"] = "Firma aquí", ["clear"] = "Borrar firma", ["agree"] = "Acepto este documento y sus condiciones.",
            ["accept"] = "Aceptar y firmar", ["reject"] = "Rechazar", ["need_name"] = "Escribe tu nombre.",
            ["need_sign"] = "Dibuja tu firma.", ["need_agree"] = "Marca la casilla de aceptación.",
            ["confirm_reject"] = "¿Rechazar este documento?", ["error"] = "No se pudo enviar. Inténtalo de nuevo.",
            ["accepted"] = "Aceptado por {0} el {1}.", ["rejected"] = "Rechazado el {1}.", ["revoked"] = "Este enlace fue retirado por quien lo envió.",
            ["expired"] = "Este enlace venció el {1}. Pide una nueva versión a quien te lo envió.", ["sending"] = "Enviando…",
            ["legal"] = "Al aceptar se registran tu nombre, tu firma, la fecha y la dirección IP desde la que respondes.",
        },
        ["en"] = new()
        {
            ["number"] = "No.", ["issued"] = "Issued", ["valid"] = "Valid until", ["customer"] = "Customer", ["seller"] = "Contact",
            ["description"] = "Description", ["qty"] = "Qty", ["unit_price"] = "Unit price", ["amount"] = "Amount",
            ["optional"] = "Optional items (not included in the total)", ["notes"] = "Terms", ["respond"] = "Your answer",
            ["name"] = "Full name", ["email"] = "Email (optional)", ["comment"] = "Comment (optional)",
            ["sign"] = "Sign here", ["clear"] = "Clear signature", ["agree"] = "I accept this document and its terms.",
            ["accept"] = "Accept and sign", ["reject"] = "Decline", ["need_name"] = "Enter your name.",
            ["need_sign"] = "Draw your signature.", ["need_agree"] = "Tick the acceptance box.",
            ["confirm_reject"] = "Decline this document?", ["error"] = "Could not send. Please try again.",
            ["accepted"] = "Accepted by {0} on {1}.", ["rejected"] = "Declined on {1}.", ["revoked"] = "The sender withdrew this link.",
            ["expired"] = "This link expired on {1}. Ask the sender for a new version.", ["sending"] = "Sending…",
            ["legal"] = "Accepting records your name, your signature, the date and the IP address you answer from.",
        },
        ["pt"] = new()
        {
            ["number"] = "N.º", ["issued"] = "Emitida", ["valid"] = "Válida até", ["customer"] = "Cliente", ["seller"] = "Atendimento",
            ["description"] = "Descrição", ["qty"] = "Qtd.", ["unit_price"] = "Preço unit.", ["amount"] = "Valor",
            ["optional"] = "Opcionais (não somam ao total)", ["notes"] = "Condições", ["respond"] = "Sua resposta",
            ["name"] = "Nome completo", ["email"] = "E-mail (opcional)", ["comment"] = "Comentário (opcional)",
            ["sign"] = "Assine aqui", ["clear"] = "Limpar assinatura", ["agree"] = "Aceito este documento e suas condições.",
            ["accept"] = "Aceitar e assinar", ["reject"] = "Recusar", ["need_name"] = "Digite seu nome.",
            ["need_sign"] = "Desenhe sua assinatura.", ["need_agree"] = "Marque a caixa de aceitação.",
            ["confirm_reject"] = "Recusar este documento?", ["error"] = "Não foi possível enviar. Tente novamente.",
            ["accepted"] = "Aceito por {0} em {1}.", ["rejected"] = "Recusado em {1}.", ["revoked"] = "Quem enviou retirou este link.",
            ["expired"] = "Este link venceu em {1}. Peça uma nova versão a quem o enviou.", ["sending"] = "Enviando…",
            ["legal"] = "Ao aceitar, registramos seu nome, sua assinatura, a data e o endereço IP de onde responde.",
        },
    };

    /// <summary>Claves de los textos (las mismas en los tres idiomas).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> TextKeys =>
        Texts.ToDictionary(t => t.Key, t => (IReadOnlyCollection<string>)t.Value.Keys);

    public static string Render(PublicLinkView view, string token, string nonce)
    {
        var lang = Texts.ContainsKey(view.Language) ? view.Language : "es";
        var t = Texts[lang];
        var culture = CultureInfo.GetCultureInfo(lang switch { "en" => "en-US", "pt" => "pt-BR", _ => "es-PE" });
        var d = view.Document;
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Money(decimal v) => E($"{d.Currency} {v.ToString("N" + d.CurrencyDecimals, culture)}");
        string Qty(decimal v) => E(v.ToString("0.###", culture));
        string Date(DateOnly v) => E(v.ToString("d", culture));
        string When(DateTimeOffset? v) => v is { } x ? x.ToString("g", culture) : "";

        var html = new StringBuilder();
        html.Append($"""
            <!doctype html>
            <html lang="{lang}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>{E(d.Title)} {E(d.Number)} · {E(d.Issuer.Name)}</title>
            <style>
            {Css}
            </style>
            </head>
            <body>
            <main>
            <header class="card">
              <div>
                <h1>{E(d.Issuer.Name)}</h1>
                <div class="muted">{E(Join(d.Issuer.TaxId, d.Issuer.Address))}</div>
                <div class="muted">{E(Join(d.Issuer.Phone, d.Issuer.Email))}</div>
              </div>
              <div class="meta">
                <div class="muted">{E(d.Title)}</div>
                <b>{E(d.Number)}</b>
                <div class="muted">{t["issued"]}: {Date(d.IssuedOn)}</div>
            """);
        if (d.ValidUntil is { } valid)
            html.Append($"""    <div class="muted">{t["valid"]}: {Date(valid)}</div>""");
        html.Append("</div></header>");

        // Estado
        var status = view.Status switch
        {
            PublicLinkStatus.Accepted => ("good", string.Format(culture, t["accepted"], E(view.SignerName), E(When(view.RespondedAt)))),
            PublicLinkStatus.Rejected => ("bad", string.Format(culture, t["rejected"], E(view.SignerName), E(When(view.RespondedAt)))),
            PublicLinkStatus.Revoked => ("bad", E(t["revoked"])),
            _ when view.Expired => ("bad", string.Format(culture, t["expired"], "", E(When(view.ExpiresAt)))),
            _ => (null, null),
        };
        if (status.Item1 is not null)
            html.Append($"""<div class="banner {status.Item1}" role="status">{status.Item2}</div>""");

        // Partes
        html.Append("""<section class="card parties">""");
        if (d.Customer is { } c)
        {
            html.Append($"""
                <div><h2>{t["customer"]}</h2><div><b>{E(c.Name)}</b></div><div class="muted">{E(Join(c.TaxId, c.Address))}</div>
                <div class="muted">{E(Join(c.Phone, c.Email))}</div></div>
                """);
        }
        if (d.SellerName is not null)
        {
            html.Append($"""
                <div><h2>{t["seller"]}</h2><div><b>{E(d.SellerName)}</b></div><div class="muted">{E(Join(d.SellerPhone, d.SellerEmail))}</div></div>
                """);
        }
        html.Append("</section>");

        // Líneas
        html.Append($"""
            <section class="card"><table>
            <thead><tr><th>{t["description"]}</th><th class="n">{t["qty"]}</th><th class="n hide">{t["unit_price"]}</th><th class="n">{t["amount"]}</th></tr></thead><tbody>
            """);
        void Line(PublicDocumentLine l, bool optional) => html.Append($"""
            <tr{(optional ? " class=\"opt\"" : "")}><td>{E(l.Description)}</td><td class="n">{Qty(l.Quantity)} {E(l.Unit)}</td><td class="n hide">{Money(l.UnitPrice)}</td><td class="n">{Money(l.Amount)}</td></tr>
            """);
        foreach (var l in d.Lines.Where(l => !l.Optional))
            Line(l, false);
        if (d.Lines.Any(l => l.Optional))
        {
            html.Append($"""<tr class="opt-head"><td colspan="4">{t["optional"]}</td></tr>""");
            foreach (var l in d.Lines.Where(l => l.Optional))
                Line(l, true);
        }
        html.Append("""</tbody></table><div class="totals">""");
        foreach (var total in d.Totals)
        {
            var cls = total.Grand ? " class=\"grand\"" : "";
            html.Append($"""<span{cls}>{E(total.Label)}</span><span{cls} style="text-align:right">{Money(total.Amount)}</span>""");
        }
        html.Append("</div></section>");

        if (!string.IsNullOrWhiteSpace(d.Notes))
            html.Append($"""<section class="card"><h2>{t["notes"]}</h2><div class="notes">{E(d.Notes)}</div></section>""");

        // Respuesta
        if (view.Status == PublicLinkStatus.Open && !view.Expired)
        {
            html.Append($"""
                <section class="card" id="respond" data-accept="../api/public/links/{E(token)}/accept" data-reject="../api/public/links/{E(token)}/reject">
                  <h2>{t["respond"]}</h2>
                  <label for="name">{t["name"]}</label><input type="text" id="name" maxlength="200" autocomplete="name">
                  <label for="email">{t["email"]}</label><input type="email" id="email" maxlength="200" autocomplete="email">
                  <label for="pad">{t["sign"]}</label><canvas id="pad" aria-label="{t["sign"]}"></canvas>
                  <button type="button" class="link" id="clear">{t["clear"]}</button>
                  <label for="comment">{t["comment"]}</label><textarea id="comment" rows="2" maxlength="1000"></textarea>
                  <label class="check"><input type="checkbox" id="agree"> <span>{t["agree"]}</span></label>
                  <p class="muted">{t["legal"]}</p>
                  <div class="error" id="error" role="alert"></div>
                  <div class="row"><button type="button" class="primary" id="accept">{t["accept"]}</button><button type="button" id="reject">{t["reject"]}</button></div>
                </section>
                <script nonce="{nonce}">
                {Script.Replace("__TEXTS__", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                  {
                      ["need_name"] = t["need_name"], ["need_sign"] = t["need_sign"], ["need_agree"] = t["need_agree"],
                      ["confirm_reject"] = t["confirm_reject"], ["error"] = t["error"], ["sending"] = t["sending"],
                                  }))}
                </script>
                """);
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }

    private const string Css = """
            :root { --bg:#f4f4f2; --card:#fff; --text:#141414; --muted:#6b6b6b; --line:#e4e4e0; --accent:#141414; --good:#1f7a43; --bad:#b42318; }
            @media (prefers-color-scheme: dark) { :root { --bg:#111; --card:#1b1b1b; --text:#f1f1ef; --muted:#a3a3a0; --line:#2e2e2c; --accent:#f1f1ef; --good:#5fd08f; --bad:#ff8a80; } }
            * { box-sizing:border-box; }
            body { margin:0; background:var(--bg); color:var(--text); font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; }
            main { max-width:860px; margin:0 auto; padding:24px 16px 48px; display:flex; flex-direction:column; gap:16px; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:14px; padding:20px; }
            header.card { display:flex; flex-wrap:wrap; justify-content:space-between; gap:16px; }
            h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:15px; margin:0 0 12px; text-transform:uppercase; letter-spacing:.06em; color:var(--muted); }
            .muted { color:var(--muted); font-size:13px; } .meta { text-align:right; } .meta b { font-size:18px; }
            .parties { display:grid; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); gap:16px; }
            table { width:100%; border-collapse:collapse; } th,td { padding:8px 6px; border-bottom:1px solid var(--line); text-align:left; vertical-align:top; }
            th { font-size:12px; color:var(--muted); font-weight:600; } td.n,th.n { text-align:right; white-space:nowrap; }
            tr.opt td { color:var(--muted); } .opt-head td { font-weight:600; color:var(--muted); padding-top:16px; }
            .totals { margin-left:auto; min-width:260px; display:grid; grid-template-columns:1fr auto; gap:6px 24px; margin-top:12px; }
            .totals .grand { font-weight:700; font-size:18px; }
            .notes { white-space:pre-wrap; }
            .banner { border-radius:12px; padding:14px 16px; font-weight:600; }
            .banner.good { background:color-mix(in srgb,var(--good) 14%,transparent); color:var(--good); }
            .banner.bad { background:color-mix(in srgb,var(--bad) 14%,transparent); color:var(--bad); }
            label { display:block; font-size:13px; font-weight:600; margin:12px 0 4px; }
            input[type=text],input[type=email],textarea { width:100%; padding:10px 12px; border:1px solid var(--line); border-radius:10px; background:var(--bg); color:var(--text); font:inherit; }
            canvas { width:100%; height:180px; border:1px dashed var(--muted); border-radius:10px; background:#fff; touch-action:none; display:block; }
            .row { display:flex; flex-wrap:wrap; gap:10px; align-items:center; margin-top:14px; }
            .check { display:flex; gap:8px; align-items:flex-start; font-weight:400; }
            button { font:inherit; font-weight:600; border-radius:10px; padding:11px 18px; border:1px solid var(--line); background:var(--card); color:var(--text); cursor:pointer; }
            button.primary { background:var(--accent); color:var(--card); border-color:var(--accent); }
            button.link { border:none; background:none; padding:4px 0; color:var(--muted); text-decoration:underline; }
            button:disabled { opacity:.6; cursor:default; }
            .error { color:var(--bad); font-weight:600; min-height:1.5em; }
            .signature { max-width:280px; background:#fff; border-radius:8px; }
            @media (max-width:600px) { .meta { text-align:left; } th.hide,td.hide { display:none; } }
        """;

    private const string Script = """
                (() => {
                  const T = __TEXTS__;
                  const box = document.getElementById('respond'), pad = document.getElementById('pad'), ctx = pad.getContext('2d');
                  const $ = id => document.getElementById(id), error = $('error');
                  let drawn = false, drawing = false;
                  const fit = () => { const r = pad.getBoundingClientRect(), k = window.devicePixelRatio || 1; pad.width = r.width * k; pad.height = r.height * k;
                    ctx.setTransform(k, 0, 0, k, 0, 0); ctx.lineWidth = 2.2; ctx.lineCap = 'round'; ctx.lineJoin = 'round'; ctx.strokeStyle = '#111'; drawn = false; };
                  const at = e => { const r = pad.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
                  pad.addEventListener('pointerdown', e => { drawing = true; pad.setPointerCapture(e.pointerId); ctx.beginPath(); ctx.moveTo(...at(e)); });
                  pad.addEventListener('pointermove', e => { if (!drawing) return; ctx.lineTo(...at(e)); ctx.stroke(); drawn = true; });
                  ['pointerup', 'pointercancel'].forEach(n => pad.addEventListener(n, () => drawing = false));
                  $('clear').addEventListener('click', fit);
                  window.addEventListener('resize', () => { if (!drawn) fit(); });
                  fit();
                  const send = async (url, body, button) => {
                    error.textContent = ''; const label = button.textContent; button.disabled = true; button.textContent = T.sending;
                    try {
                      const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
                      if (r.ok || r.status === 409 || r.status === 410) { location.reload(); return; }
                      error.textContent = T.error;
                    } catch { error.textContent = T.error; }
                    button.disabled = false; button.textContent = label;
                  };
                  $('accept').addEventListener('click', e => {
                    const name = $('name').value.trim();
                    if (name.length < 2) { error.textContent = T.need_name; return; }
                    if (!drawn) { error.textContent = T.need_sign; return; }
                    if (!$('agree').checked) { error.textContent = T.need_agree; return; }
                    send(box.dataset.accept, { signerName: name, signerEmail: $('email').value.trim() || null, comment: $('comment').value.trim() || null,
                      signature: pad.toDataURL('image/png') }, e.currentTarget);
                  });
                  $('reject').addEventListener('click', e => {
                    if (!confirm(T.confirm_reject)) return;
                    send(box.dataset.reject, { signerName: $('name').value.trim() || null, comment: $('comment').value.trim() || null }, e.currentTarget);
                  });
                })();
        """;

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
