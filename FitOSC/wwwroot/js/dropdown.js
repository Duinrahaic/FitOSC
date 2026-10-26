const menus = new Map();

export function initialize(root) {
    if (!root) return;
    const id = root.querySelector('.app-dropdown-trigger').id;
    if (menus.has(id)) {
        menus.get(id).refresh();
        return;
    }

    const trigger = root.querySelector('.app-dropdown-trigger');
    const list = root.querySelector('.app-dropdown-menu');
    const events = new AbortController();
    const on = (target, name, callback, capture = false) =>
        target.addEventListener(name, callback, { signal: events.signal, capture });
    const options = () => Array.from(list.querySelectorAll('.app-dropdown-option:not(:disabled)'));
    let active = null;
    let prefix = '';
    let lastTyped = 0;

    function position() {
        const rect = trigger.getBoundingClientRect();
        const gap = 4;
        const margin = 8;
        const below = Math.max(0, innerHeight - rect.bottom - gap - margin);
        const above = Math.max(0, rect.top - gap - margin);
        const width = Math.min(Math.max(rect.width, 190), innerWidth - margin * 2);
        list.style.width = width + 'px';
        list.style.left = Math.max(margin, Math.min(rect.left, innerWidth - width - margin)) + 'px';
        const desired = Math.min(264, list.scrollHeight);
        const openBelow = below >= desired || below >= above;
        list.style.maxHeight = Math.min(264, openBelow ? below : above) + 'px';
        list.style.top = (openBelow ? rect.bottom + gap : rect.top - gap - list.offsetHeight) + 'px';
    }

    function highlight(option) {
        active?.classList.remove('is-active');
        active = option;
        if (active) {
            active.classList.add('is-active');
            trigger.setAttribute('aria-activedescendant', active.id);
            // Scroll the menu, without scrolling the settings pane behind it.
            if (active.offsetTop < list.scrollTop) list.scrollTop = active.offsetTop;
            if (active.offsetTop + active.offsetHeight > list.scrollTop + list.clientHeight)
                list.scrollTop = active.offsetTop + active.offsetHeight - list.clientHeight;
        } else {
            trigger.removeAttribute('aria-activedescendant');
        }
    }

    function close(restoreFocus = false) {
        list.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');
        highlight(null);
        prefix = '';
        if (restoreFocus) trigger.focus({ preventScroll: true });
    }

    function open() {
        if (trigger.disabled || options().length === 0) return;
        list.hidden = false;
        trigger.setAttribute('aria-expanded', 'true');
        position();
        const available = options();
        highlight(available.find(option => option.getAttribute('aria-selected') === 'true') ?? available[0]);
    }

    on(trigger, 'click', () => list.hidden ? open() : close());
    on(list, 'click', event => {
        if (event.target.closest('.app-dropdown-option:not(:disabled)')) close(true);
    });
    on(list, 'pointermove', event => {
        const option = event.target.closest('.app-dropdown-option:not(:disabled)');
        if (option && option !== active) highlight(option);
    });
    on(document, 'pointerdown', event => { if (!root.contains(event.target)) close(); });
    on(document, 'focusin', event => { if (!root.contains(event.target)) close(); });
    on(window, 'resize', () => { if (!list.hidden) position(); });
    on(document, 'scroll', event => {
        if (!list.hidden && !list.contains(event.target)) close();
    }, true);

    on(trigger, 'keydown', event => {
        const available = options();
        if (event.key === 'Tab') { close(); return; }
        if (event.key === 'Escape' && !list.hidden) {
            event.preventDefault(); event.stopPropagation(); close(true); return;
        }
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            if (list.hidden) open();
            else active?.click();
            return;
        }
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            event.preventDefault();
            if (list.hidden) {
                open();
                if (event.key === 'Home') highlight(available[0]);
                if (event.key === 'End') highlight(available.at(-1));
                return;
            }
            if (!available.length) return;
            const index = available.indexOf(active);
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? available.length - 1
                : (index + (event.key === 'ArrowUp' ? -1 : 1) + available.length) % available.length;
            highlight(available[next]);
            return;
        }
        if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
            event.preventDefault();
            if (list.hidden) open();
            prefix = (Date.now() - lastTyped < 700 ? prefix : '') + event.key.toLocaleLowerCase();
            lastTyped = Date.now();
            const match = available.find(option => option.querySelector('.app-dropdown-label').textContent.trim().toLocaleLowerCase().startsWith(prefix));
            if (match) highlight(match);
        }
    });

    menus.set(id, {
        refresh() {
            if (trigger.disabled) close();
            else if (!list.hidden) { position(); highlight(options().includes(active) ? active : options()[0]); }
        },
        destroy() { close(); events.abort(); }
    });
}

export function destroy(id) {
    menus.get(id)?.destroy();
    menus.delete(id);
}
