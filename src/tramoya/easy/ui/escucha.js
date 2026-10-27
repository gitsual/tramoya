// Lo que el grabador de `tramoya easy` inyecta en cada página: cada clic, texto,
// opción o tecla de la persona, con el selector que genera Playwright.
(() => {
  if (window.__tramoyaEscucha) return;
  window.__tramoyaEscucha = true;
  const ROL = {BUTTON: "el botón", A: "el enlace", TD: "la celda", TH: "la columna", IMG: "la imagen"};
  const ROLES = {button: "el botón", link: "el enlace", tab: "la pestaña", menuitem: "la opción del menú",
                 option: "la opción", checkbox: "la casilla", radio: "la opción", gridcell: "la celda",
                 cell: "la celda", treeitem: "el elemento", switch: "el interruptor", combobox: "el desplegable"};
  const limpio = (t) => (t || "").replace(/\s+/g, " ").trim().slice(0, 70);
  const sel = (el) => { try { return window.playwright.selector(el); } catch (e) { return ""; } };
  const nombre = (el) => limpio(el.getAttribute("aria-label") || (el.labels && el.labels[0] && el.labels[0].innerText)
      || el.innerText || el.value || el.title || el.placeholder || el.alt || el.name);
  const tipoDe = (el) => {
    const r = el.getAttribute("role");
    if (r && ROLES[r]) return ROLES[r];
    if (el.tagName === "INPUT") {
      const t = (el.type || "").toLowerCase();
      if (t === "checkbox") return "la casilla";
      if (t === "radio") return "la opción";
      if (["button", "submit", "reset", "image"].includes(t)) return "el botón";
      return "el campo";
    }
    if (el.tagName === "TEXTAREA") return "el campo";
    return ROL[el.tagName] || "";
  };
  let pendiente = null;   // el último campo en el que se ha escrito y aún no se ha apuntado
  const enviar = (d) => {
    if (d.tipo !== "escribir" && pendiente) { const p = pendiente; pendiente = null; escribir(p); }
    try { window.__tramoyaPaso(d); } catch (e) {}
  };
  const rellenable = (el) => el && ((el.tagName === "INPUT" && !["checkbox", "radio", "button", "submit",
      "reset", "image", "file", "hidden", "range", "color"].includes((el.type || "").toLowerCase()))
      || el.tagName === "TEXTAREA");
  const enviado = new WeakMap();
  const escribir = (el) => {
    if (el === pendiente) pendiente = null;
    if (!rellenable(el) || enviado.get(el) === el.value) return;
    enviado.set(el, el.value);
    if ((el.type || "").toLowerCase() === "password") { enviar({tipo: "clave"}); return; }
    enviar({tipo: "escribir", selector: sel(el), valor: el.value, nombre: nombre(el), que: "el campo"});
  };
  document.addEventListener("click", (e) => {
    if (!e.isTrusted) return;
    const el = e.target.closest("button, a, [role=button], [role=link], [role=tab], [role=menuitem], [role=option], " +
      "[role=checkbox], [role=radio], [role=gridcell], [role=treeitem], [role=switch], input, select, textarea, " +
      "summary, label, td, th, li, img, [onclick]") || e.target;
    if (el.tagName === "LABEL" && el.control) return;        // el clic «de verdad» llega a su casilla
    if (el.tagName === "SELECT") return;                      // lo apunta el cambio de opción
    if (rellenable(el) && enviado.has(el)) return;            // volver a un campo ya escrito
    const t = (el.type || "").toLowerCase();
    enviar({tipo: "clic", selector: sel(el), nombre: nombre(el), que: tipoDe(el),
            marca: el.tagName === "INPUT" && (t === "checkbox" || t === "radio") ? (el.checked ? "on" : "off") : ""});
  }, true);
  document.addEventListener("input", (e) => { if (rellenable(e.target)) pendiente = e.target; }, true);
  document.addEventListener("change", (e) => {
    const el = e.target;
    if (el.tagName === "SELECT") {
      const o = el.options[el.selectedIndex];
      enviar({tipo: "elegir", selector: sel(el), valor: el.value, texto_opcion: limpio(o && o.text), nombre: nombre(el)});
    } else escribir(el);
  }, true);
  document.addEventListener("keydown", (e) => {
    if (!e.isTrusted || !["Enter", "Tab", "Escape"].includes(e.key)) return;
    if (e.key === "Enter" && e.target.tagName === "TEXTAREA") return;
    escribir(e.target);
    enviar({tipo: "tecla", valor: e.key});
  }, true);
})();
