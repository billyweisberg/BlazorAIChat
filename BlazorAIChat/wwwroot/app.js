import DOMPurify from './lib/dompurify/dist/purify.es.mjs';
import * as marked from './lib/marked/dist/marked.esm.js';

const purify = DOMPurify(window);

customElements.define('assistant-message', class extends HTMLElement {
    static observedAttributes = ['markdown'];
    attributeChangedCallback(name, oldValue, newValue) {
        if (name === 'markdown') {

            // Remove <citation> tags
            newValue = newValue.replace(/<citation.*?<\/citation>/gs, '');

            // Parse the markdown to HTML
            const elements = marked.parse(newValue);

            // Sanitize the HTML
            const sanitizedElements = purify.sanitize(elements, { KEEP_CONTENT: false });

            // Escape HTML code blocks
            const escapedHtml = sanitizedElements.replace(/<code>(.*?)<\/code>/gs, (match, p1) => {
                return `<code>${p1.replace(/</g, '&lt;').replace(/>/g, '&gt;')}</code>`;
            });

            // Set the innerHTML
            this.innerHTML = escapedHtml;
        }
    }
});

window.focusElement = function (el) { if (!el) return; setTimeout(() => { try { el.focus(); } catch { } }, 0); };

// Legacy helpers (kept if referenced elsewhere)
window.measureAndResize = function (el) { if (!el) return 0; el.style.height = 'auto'; const h = el.scrollHeight; el.style.height = h + 'px'; return h; };
window.autoResize = function (el) { if (!el) return; el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; };

function resizeWithLimit(el) {
    if (!el) return;
    const lineHeight = parseFloat(getComputedStyle(el).lineHeight) || 20;
    const maxRows = parseInt(el.getAttribute('data-max-rows') || '0', 10);
    const maxHeight = maxRows > 0 ? (lineHeight * maxRows) + 8 : null; // +8 padding fudge
    el.style.height = 'auto';
    const needed = el.scrollHeight;
    if (maxHeight && needed > maxHeight) {
        el.style.height = maxHeight + 'px';
        el.style.overflowY = 'auto';
    } else {
        el.style.height = needed + 'px';
        el.style.overflowY = 'hidden';
    }
}

window.attachAutoGrow = function (el) {
    if (!el || el._autoGrowAttached) return;
    el._autoGrowAttached = true;
    const handler = () => resizeWithLimit(el);
    el.addEventListener('input', handler);
    el.addEventListener('change', handler);
    // initial
    handler();
};

window.triggerAutoGrow = function (el) { resizeWithLimit(el); };
