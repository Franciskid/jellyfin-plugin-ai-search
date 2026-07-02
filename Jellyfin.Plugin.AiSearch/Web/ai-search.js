(function () {
    'use strict';

    if (window.__aiSearchLoaded) {
        return;
    }
    window.__aiSearchLoaded = true;

    var STYLES = [
        '.ais-fab{position:fixed;right:22px;bottom:22px;z-index:1000;width:56px;height:56px;border-radius:50%;',
        'border:none;cursor:pointer;background:#00a4dc;color:#fff;box-shadow:0 4px 14px rgba(0,0,0,.4);',
        'font-size:24px;display:flex;align-items:center;justify-content:center;transition:transform .15s ease;}',
        '.ais-fab:hover{transform:scale(1.06);}',
        '.ais-overlay{position:fixed;inset:0;z-index:1001;background:rgba(0,0,0,.6);display:flex;',
        'align-items:flex-start;justify-content:center;padding:8vh 16px 16px;}',
        '.ais-panel{width:100%;max-width:760px;max-height:82vh;overflow:auto;background:#101418;color:#eee;',
        'border-radius:12px;box-shadow:0 10px 40px rgba(0,0,0,.6);padding:20px;}',
        '.ais-title{font-size:1.15em;font-weight:600;margin:0 0 12px;display:flex;align-items:center;gap:8px;}',
        '.ais-row{display:flex;gap:8px;}',
        '.ais-input{flex:1;padding:12px 14px;border-radius:8px;border:1px solid #2a3138;background:#171c22;',
        'color:#fff;font-size:1em;outline:none;}',
        '.ais-input:focus{border-color:#00a4dc;}',
        '.ais-go{padding:0 18px;border-radius:8px;border:none;background:#00a4dc;color:#fff;cursor:pointer;font-size:1em;}',
        '.ais-go:disabled{opacity:.6;cursor:default;}',
        '.ais-hint{opacity:.6;font-size:.85em;margin:10px 2px 0;}',
        '.ais-answer{margin:16px 0 8px;font-size:1.02em;opacity:.95;}',
        '.ais-list{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:14px;margin-top:12px;}',
        '.ais-card{cursor:pointer;background:#171c22;border-radius:10px;overflow:hidden;transition:transform .12s ease;}',
        '.ais-card:hover{transform:translateY(-3px);}',
        '.ais-poster{width:100%;aspect-ratio:2/3;object-fit:cover;background:#222;display:block;}',
        '.ais-meta{padding:8px 9px;}',
        '.ais-name{font-size:.9em;font-weight:600;line-height:1.2;}',
        '.ais-year{font-size:.78em;opacity:.6;}',
        '.ais-reason{font-size:.8em;opacity:.85;margin-top:5px;line-height:1.25;}',
        '.ais-err{color:#ff7676;margin-top:14px;}',
        '.ais-spin{margin-top:16px;opacity:.8;}',
        '.ais-close{margin-left:auto;background:none;border:none;color:#aaa;font-size:1.4em;cursor:pointer;line-height:1;}'
    ].join('');

    function api() {
        return window.ApiClient;
    }

    // Follow the Jellyfin web client's language — French or English only. The web
    // client sets <html lang>; fall back to the browser language.
    function isFrench() {
        var lang = (document.documentElement.getAttribute('lang')
            || (navigator && (navigator.language || navigator.userLanguage))
            || 'en');
        return /^fr/i.test(lang);
    }

    var STRINGS = {
        en: {
            fabTitle: 'AI search (recommend from my library)',
            panelTitle: '✨ AI search',
            close: 'Close',
            placeholder: 'e.g. something tense and short for tonight, like Sicario',
            hint: 'Recommends movies from your own library, personalized to your history.',
            search: 'Search',
            thinking: 'Thinking…',
            noMatches: 'No matches found in your library. Try rephrasing.',
            unavailable: 'AI search is unavailable right now.',
            disabled: 'AI search is not configured or disabled.',
            timeout: 'The AI service timed out. Try again.'
        },
        fr: {
            fabTitle: 'Recherche IA (recommandations de ma bibliothèque)',
            panelTitle: '✨ Recherche IA',
            close: 'Fermer',
            placeholder: 'ex. quelque chose de tendu et court pour ce soir, comme Sicario',
            hint: 'Recommande des films de votre bibliothèque, selon votre historique.',
            search: 'Rechercher',
            thinking: 'Réflexion…',
            noMatches: 'Aucun résultat dans votre bibliothèque. Reformulez votre demande.',
            unavailable: 'La recherche IA est indisponible pour le moment.',
            disabled: 'La recherche IA n’est pas configurée ou est désactivée.',
            timeout: 'Le service IA a expiré. Réessayez.'
        }
    };

    function t() {
        return isFrench() ? STRINGS.fr : STRINGS.en;
    }

    function ready(cb) {
        var tries = 0;
        var t = setInterval(function () {
            tries++;
            var a = api();
            if (a && typeof a.accessToken === 'function' && a.accessToken()) {
                clearInterval(t);
                cb();
            } else if (tries > 120) {
                clearInterval(t);
            }
        }, 800);
    }

    function injectStyles() {
        if (document.getElementById('ais-styles')) {
            return;
        }
        var s = document.createElement('style');
        s.id = 'ais-styles';
        s.textContent = STYLES;
        document.head.appendChild(s);
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function navigate(id) {
        var a = api();
        var serverId = a && a.serverId ? a.serverId() : '';
        var route = 'details?id=' + id + (serverId ? '&serverId=' + serverId : '');
        try {
            if (window.Dashboard && typeof Dashboard.navigate === 'function') {
                Dashboard.navigate(route);
                return;
            }
        } catch (e) { /* fall through */ }
        window.location.hash = '#/' + route;
    }

    var overlay;

    function closePanel() {
        if (overlay && overlay.parentNode) {
            overlay.parentNode.removeChild(overlay);
        }
        overlay = null;
    }

    function render(container, data) {
        var html = '';
        if (data.answer) {
            html += '<div class="ais-answer">' + esc(data.answer) + '</div>';
        }
        var results = data.results || [];
        if (!results.length) {
            html += '<div class="ais-hint">' + esc(t().noMatches) + '</div>';
            container.innerHTML = html;
            return;
        }
        html += '<div class="ais-list">';
        results.forEach(function (r) {
            var img = api().getImageUrl(r.itemId, { type: 'Primary', maxHeight: 260, quality: 90 });
            html += '<div class="ais-card" data-id="' + esc(r.itemId) + '">'
                + '<img class="ais-poster" loading="lazy" src="' + esc(img) + '" onerror="this.style.visibility=\'hidden\'" />'
                + '<div class="ais-meta">'
                + '<div class="ais-name">' + esc(r.title) + '</div>'
                + (r.year ? '<div class="ais-year">' + esc(r.year) + '</div>' : '')
                + (r.reason ? '<div class="ais-reason">' + esc(r.reason) + '</div>' : '')
                + '</div></div>';
        });
        html += '</div>';
        container.innerHTML = html;
        Array.prototype.forEach.call(container.querySelectorAll('.ais-card'), function (card) {
            card.addEventListener('click', function () {
                closePanel();
                navigate(card.getAttribute('data-id'));
            });
        });
    }

    function search(prompt, results, goBtn) {
        var s = t();
        results.innerHTML = '<div class="ais-spin">' + esc(s.thinking) + '</div>';
        goBtn.disabled = true;
        api().ajax({
            type: 'POST',
            url: api().getUrl('AiSearch/Recommend'),
            data: JSON.stringify({ prompt: prompt, locale: isFrench() ? 'fr' : 'en' }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (data) {
            goBtn.disabled = false;
            render(results, data);
        }).catch(function (err) {
            goBtn.disabled = false;
            var msg = s.unavailable;
            try {
                if (err && err.status === 503) { msg = s.disabled; }
                else if (err && err.status === 504) { msg = s.timeout; }
            } catch (e) { /* ignore */ }
            results.innerHTML = '<div class="ais-err">' + esc(msg) + '</div>';
        });
    }

    function openPanel() {
        if (overlay) {
            return;
        }
        var s = t();
        overlay = document.createElement('div');
        overlay.className = 'ais-overlay';
        overlay.innerHTML =
            '<div class="ais-panel">'
            + '<div class="ais-title">' + esc(s.panelTitle)
            + '<button class="ais-close" title="' + esc(s.close) + '">×</button></div>'
            + '<div class="ais-row">'
            + '<input class="ais-input" type="text" placeholder="' + esc(s.placeholder) + '" />'
            + '<button class="ais-go">' + esc(s.search) + '</button>'
            + '</div>'
            + '<div class="ais-hint">' + esc(s.hint) + '</div>'
            + '<div class="ais-results"></div>'
            + '</div>';
        document.body.appendChild(overlay);

        var panel = overlay.querySelector('.ais-panel');
        var input = overlay.querySelector('.ais-input');
        var goBtn = overlay.querySelector('.ais-go');
        var results = overlay.querySelector('.ais-results');

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) { closePanel(); }
        });
        panel.addEventListener('click', function (e) { e.stopPropagation(); });
        overlay.querySelector('.ais-close').addEventListener('click', closePanel);
        goBtn.addEventListener('click', function () {
            var q = input.value.trim();
            if (q) { search(q, results, goBtn); }
        });
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                var q = input.value.trim();
                if (q) { search(q, results, goBtn); }
            } else if (e.key === 'Escape') {
                closePanel();
            }
        });
        setTimeout(function () { input.focus(); }, 50);
    }

    var fab;
    // Whether the plugin reports itself enabled + configured (from AiSearch/Health).
    var usable = false;

    // True while the built-in video player is active — the launcher must not sit
    // on top of a movie. The player mounts a hash route + a container element.
    function isPlaying() {
        var hash = (window.location.hash || '').toLowerCase();
        if (hash.indexOf('/video') !== -1) {
            return true;
        }
        var players = document.querySelectorAll('.videoPlayerContainer:not(.hide)');
        for (var i = 0; i < players.length; i++) {
            if (players[i] && players[i].offsetParent !== null) {
                return true;
            }
        }
        return false;
    }

    function ensureFab() {
        if (fab && fab.parentNode) {
            return;
        }
        fab = document.createElement('button');
        fab.className = 'ais-fab';
        fab.title = t().fabTitle;
        fab.textContent = '✨';
        fab.addEventListener('click', openPanel);
        document.body.appendChild(fab);
    }

    function removeFab() {
        if (fab && fab.parentNode) {
            fab.parentNode.removeChild(fab);
        }
        fab = null;
    }

    // Single source of truth for launcher visibility: show only when the feature
    // is enabled/configured AND no video is playing; otherwise remove it entirely.
    function updateLauncher() {
        if (usable && !isPlaying()) {
            ensureFab();
        } else {
            removeFab();
            if (!usable) {
                closePanel();
            }
        }
    }

    function refreshHealth() {
        var a = api();
        if (!a || typeof a.ajax !== 'function' || typeof a.getUrl !== 'function') {
            return;
        }
        a.ajax({ type: 'GET', url: a.getUrl('AiSearch/Health'), dataType: 'json' })
            .then(function (h) {
                usable = !!(h && h.enabled && h.configured);
                updateLauncher();
            })
            .catch(function () { /* keep last known state on transient errors */ });
    }

    ready(function () {
        injectStyles();
        refreshHealth();
        // React promptly to SPA navigation and playback start/stop.
        window.addEventListener('hashchange', updateLauncher);
        setInterval(updateLauncher, 1500);
        // Pick up config changes (Enabled toggled in the dashboard) without a reload.
        setInterval(refreshHealth, 15000);
    });
})();
