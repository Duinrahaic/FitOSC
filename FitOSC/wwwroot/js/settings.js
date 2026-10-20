const initialized = new WeakSet();

export function initialize(root) {
    if (!root || initialized.has(root)) return;
    initialized.add(root);

    root.addEventListener('keydown', event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
            event.preventDefault();
            const save = root.querySelector('[data-settings-save]');
            if (!save.disabled) save.click();
            return;
        }

        const current = event.target.closest('.settings-menu > button');
        if (!current) return;
        const buttons = Array.from(root.querySelectorAll('.settings-menu > button'));
        const index = buttons.indexOf(current);
        let next;
        switch (event.key) {
            case 'ArrowDown': next = (index + 1) % buttons.length; break;
            case 'ArrowUp': next = (index - 1 + buttons.length) % buttons.length; break;
            case 'Home': next = 0; break;
            case 'End': next = buttons.length - 1; break;
            default: return;
        }
        event.preventDefault();
        buttons[next].focus();
        buttons[next].click();
    });
}
