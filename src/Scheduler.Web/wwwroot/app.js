// For interactive pages: the browser's IANA time zone, e.g. to default the organizer's zone when composing.
window.scheduler = { timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone };

// Progressive enhancement for static pages: shows each proposed time in the viewer's own zone, next to the
// organizer's. Pages work without it. Elements: <time datetime="UTC" data-viewer-local data-zone="Organizer/Zone" hidden>.
(() => {
    const showViewerTimes = () => {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
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
