// For interactive pages: the browser's IANA time zone, e.g. to default the organizer's zone when composing.
window.scheduler = { timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone };

// Progressive enhancement for static pages: shows each proposed time in the viewer's own zone, next to the
// organizer's, and fills zone fields marked data-viewer-zone with the viewer's zone. Pages work without it.
// Elements: <time datetime="UTC" data-viewer-local data-zone="Organizer/Zone" hidden>, <input data-viewer-zone>.
(() => {
    const showViewerTimes = () => {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        // A zone field for times the viewer types (suggesting a time) defaults to their own zone.
        for (const input of document.querySelectorAll('input[data-viewer-zone]')) {
            if (zone) input.value = zone;
        }
        for (const element of document.querySelectorAll('time[data-viewer-local]')) {
            if (!zone || element.dataset.zone === zone) {
                element.hidden = true;
                continue;
            }
            const when = new Date(element.dateTime);
            if (Number.isNaN(when.getTime())) continue;
            element.textContent = 'your time: ' + when.toLocaleString(undefined, {
                weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short',
            });
            element.hidden = false;
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', showViewerTimes);
    } else {
        showViewerTimes();
    }
    // Blazor's enhanced navigation swaps page content without a full load.
    window.Blazor?.addEventListener?.('enhancedload', showViewerTimes);
})();

// StatusNotice on static pages: interactive pages close a notice themselves; on a page that only posts forms, the
// close button stays hidden unless this script is here to handle it.
(() => {
    const showDismissButtons = () => {
        for (const button of document.querySelectorAll('[data-notice-dismiss][hidden]')) {
            button.hidden = false;
        }
    };

    document.addEventListener('click', event => {
        const button = event.target instanceof Element ? event.target.closest('[data-notice-dismiss]') : null;
        button?.closest('.notice')?.remove();
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', showDismissButtons);
    } else {
        showDismissButtons();
    }
    window.Blazor?.addEventListener?.('enhancedload', showDismissButtons);
})();
