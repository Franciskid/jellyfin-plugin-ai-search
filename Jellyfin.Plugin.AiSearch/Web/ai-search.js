(function () {
    'use strict';

    if (window.__aiSearchLoaded) {
        return;
    }
    window.__aiSearchLoaded = true;

    // ---------------------------------------------------------------- i18n ---

    function isFrench() {
        var lang = (document.documentElement.getAttribute('lang')
            || (navigator && (navigator.language || navigator.userLanguage))
            || 'en');
        return /^fr/i.test(lang);
    }

    var STRINGS = {
        en: {
            fabTitle: 'AI search',
            title: 'AI search',
            close: 'Close',
            back: 'Back',
            search: 'Search',
            surprise: 'Surprise me',
            collection: 'Create collection',
            helpMe: 'Help me choose',
            placeholder: 'Describe what you feel like watching',
            collectionPlaceholder: 'Describe a collection, e.g. rainy Sunday afternoon',
            recent: 'Recent',
            clear: 'Clear',
            emptyTitle: 'Ask in your own words',
            emptyBody: 'Describe a mood, a vibe, a night in. Or let the buttons decide.',
            examples: ['tense and short, like Sicario', 'a feel-good 90s comedy', 'visually stunning sci-fi'],
            movies: function (n) { return n + (n === 1 ? ' film' : ' films'); },
            noMatches: 'Nothing in your library fits that. Try rephrasing.',
            unavailable: 'AI search is unavailable right now.',
            disabled: 'AI search is not configured.',
            timeout: 'The AI service timed out. Try again.',
            more: 'More like this',
            searchAgain: 'Search again',
            savePlaceholder: 'Collection name',
            save: 'Save',
            saveResults: 'Save to playlist',
            saving: 'Saving',
            saved: function (name) { return 'Saved to ' + name; },
            saveFailed: 'Could not create the playlist.',
            surprising: 'Finding something unexpected',
            loading: ['Understanding your request', 'Searching your library', 'Choosing the best matches'],
            preparing: 'Thinking of the right questions',
            refine: 'A few questions to narrow it down',
            personalize: 'Personalize to my taste',
            scopeMovies: 'Movies',
            scopeTv: 'TV Shows',
            q1: 'What kind of night is it?',
            q1opts: [['easy', 'Easy'], ['intense', 'Intense'], ['beautiful', 'Beautiful'], ['funny', 'Funny'], ['dark', 'Dark']],
            q2: 'How much time?',
            q2opts: [['short', 'Under 90 min'], ['normal', 'Around 2 hours'], ['any', "Doesn't matter"]],
            q3: 'Seen it already?',
            q3opts: [['unseen', 'Something new'], ['rewatch', 'Can rewatch a favorite']],
            go: 'Find it'
        },
        fr: {
            fabTitle: 'Recherche IA',
            title: 'Recherche IA',
            close: 'Fermer',
            back: 'Retour',
            search: 'Rechercher',
            surprise: 'Surprends-moi',
            collection: 'Créer une collection',
            helpMe: 'Aide-moi à choisir',
            placeholder: 'Décrivez ce que vous avez envie de regarder',
            collectionPlaceholder: 'Décrivez une collection, ex. dimanche pluvieux',
            recent: 'Récent',
            clear: 'Effacer',
            emptyTitle: 'Demandez avec vos mots',
            emptyBody: 'Décrivez une envie, une ambiance, une soirée. Ou laissez les boutons choisir.',
            examples: ['tendu et court, comme Sicario', 'une comédie feel-good des années 90', 'de la SF visuellement magnifique'],
            movies: function (n) { return n + (n === 1 ? ' film' : ' films'); },
            noMatches: 'Rien dans votre bibliothèque ne correspond. Reformulez.',
            unavailable: 'La recherche IA est indisponible pour le moment.',
            disabled: 'La recherche IA n’est pas configurée.',
            timeout: 'Le service IA a expiré. Réessayez.',
            more: 'Plus comme ça',
            searchAgain: 'Relancer',
            savePlaceholder: 'Nom de la collection',
            save: 'Enregistrer',
            saveResults: 'Enregistrer en playlist',
            saving: 'Enregistrement',
            saved: function (name) { return 'Enregistré dans ' + name; },
            saveFailed: 'Impossible de créer la playlist.',
            surprising: 'Je cherche une surprise',
            loading: ['Je comprends votre demande', 'Je parcours votre bibliothèque', 'Je choisis les meilleurs films'],
            preparing: 'Je prépare les bonnes questions',
            refine: 'Quelques questions pour affiner',
            personalize: 'Selon mes goûts',
            scopeMovies: 'Films',
            scopeTv: 'Séries',
            q1: 'Quelle soirée ?',
            q1opts: [['easy', 'Détente'], ['intense', 'Intense'], ['beautiful', 'Beau'], ['funny', 'Drôle'], ['dark', 'Sombre']],
            q2: 'Combien de temps ?',
            q2opts: [['short', 'Moins de 90 min'], ['normal', 'Environ 2 h'], ['any', 'Peu importe']],
            q3: 'Déjà vu ?',
            q3opts: [['unseen', 'Du nouveau'], ['rewatch', 'Un favori à revoir']],
            go: 'Trouver'
        }
    };

    function t() { return isFrench() ? STRINGS.fr : STRINGS.en; }

    function interviewPrompt(a) {
        var fr = isFrench();
        var mood = {
            easy: fr ? 'léger et détendu' : 'easy-going and relaxing',
            intense: fr ? 'intense et prenant' : 'intense and gripping',
            beautiful: fr ? 'visuellement magnifique' : 'visually beautiful',
            funny: fr ? 'drôle' : 'funny',
            dark: fr ? 'sombre' : 'dark'
        }[a.mood];
        var time = { short: fr ? 'moins de 90 minutes' : 'under 90 minutes', normal: fr ? 'environ deux heures' : 'about two hours', any: '' }[a.time];
        var parts = [];
        if (mood) { parts.push(mood); }
        if (time) { parts.push(time); }
        var joined = parts.join(', ');
        return (fr ? 'Un film ' : 'A movie that is ') + (joined || (fr ? 'que je vais aimer' : "I'll enjoy")) + '.';
    }

    function relTime(iso) {
        var fr = isFrench();
        var s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (s < 60) { return fr ? "à l'instant" : 'just now'; }
        var m = Math.floor(s / 60);
        if (m < 60) { return m + ' min'; }
        var h = Math.floor(m / 60);
        if (h < 24) { return h + ' h'; }
        return Math.floor(h / 24) + (fr ? ' j' : 'd');
    }

    // ------------------------------------------------------------- helpers ---

    function api() { return window.ApiClient; }
    function locale() { return isFrench() ? 'fr' : 'en'; }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function imageUrl(id) {
        try { return api().getImageUrl(id, { type: 'Primary', maxHeight: 330, quality: 90 }); }
        catch (e) { return ''; }
    }

    function navigate(id) {
        var a = api();
        var serverId = a && a.serverId ? a.serverId() : '';
        var route = 'details?id=' + id + (serverId ? '&serverId=' + serverId : '');
        try {
            if (window.Dashboard && typeof Dashboard.navigate === 'function') { Dashboard.navigate(route); return; }
        } catch (e) { /* fall through */ }
        window.location.hash = '#/' + route;
    }

    function ready(cb) {
        var tries = 0;
        var timer = setInterval(function () {
            tries++;
            var a = api();
            if (a && typeof a.accessToken === 'function' && a.accessToken()) { clearInterval(timer); cb(); }
            else if (tries > 120) { clearInterval(timer); }
        }, 800);
    }

    // ------------------------------------------------------------- network ---

    function recommend(opts) {
        var body = { prompt: opts.prompt || '', locale: locale(), mode: opts.server || 'normal', personalize: prefs.personalize, scope: prefs.scope };
        if (typeof opts.includeWatched === 'boolean') { body.includeWatched = opts.includeWatched; }
        if (opts.excludeItemIds && opts.excludeItemIds.length) { body.excludeItemIds = opts.excludeItemIds; }
        return api().ajax({ type: 'POST', url: api().getUrl('AiSearch/Recommend'), data: JSON.stringify(body), contentType: 'application/json', dataType: 'json' });
    }

    function getInterview(prompt) {
        return api().ajax({
            type: 'POST', url: api().getUrl('AiSearch/Interview'),
            data: JSON.stringify({ prompt: prompt, locale: locale() }), contentType: 'application/json', dataType: 'json'
        }).then(function (r) { return (r && r.questions) || []; });
    }

    function loadHistory() {
        return api().ajax({ type: 'GET', url: api().getUrl('AiSearch/History'), dataType: 'json' })
            .then(function (r) { return (r && r.entries) || []; })
            .catch(function () { return []; });
    }

    function deleteHistory(id) {
        var url = api().getUrl('AiSearch/History' + (id ? '/' + encodeURIComponent(id) : ''));
        return api().ajax({ type: 'DELETE', url: url }).catch(function () { });
    }

    function createPlaylist(name, itemIds) {
        return api().ajax({
            type: 'POST', url: api().getUrl('AiSearch/Playlist'),
            data: JSON.stringify({ name: name, itemIds: itemIds }), contentType: 'application/json', dataType: 'json'
        });
    }

    // --------------------------------------------------------------- icons ---

    var IC = {
        sparkle: '<svg class="ais-ic" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 2.2l1.7 5.6a3 3 0 0 0 2 2L21.4 12l-5.7 1.7a3 3 0 0 0-2 2L12 21.8l-1.7-5.7a3 3 0 0 0-2-2L2.6 12l5.7-1.7a3 3 0 0 0 2-2z"/></svg>',
        close: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>',
        back: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M15 5l-7 7 7 7"/></svg>',
        dice: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><rect x="4" y="4" width="16" height="16" rx="4.5"/><circle cx="9" cy="9" r="1.15" fill="currentColor" stroke="none"/><circle cx="15" cy="9" r="1.15" fill="currentColor" stroke="none"/><circle cx="9" cy="15" r="1.15" fill="currentColor" stroke="none"/><circle cx="15" cy="15" r="1.15" fill="currentColor" stroke="none"/></svg>',
        layers: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" aria-hidden="true"><path d="M12 3.4l8.4 4.2-8.4 4.2-8.4-4.2z"/><path d="M3.6 12l8.4 4.2 8.4-4.2"/><path d="M3.6 16.2l8.4 4.2 8.4-4.2" opacity=".5"/></svg>',
        wand: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true"><path d="M6 18L15 9"/><path d="M15.5 3.6l.9 2.5 2.5.9-2.5.9-.9 2.5-.9-2.5-2.5-.9 2.5-.9z" fill="currentColor" stroke="none"/></svg>',
        clock: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true"><circle cx="12" cy="12" r="8"/><path d="M12 8v4l2.8 1.7"/></svg>',
        send: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 12h13M13 6l6 6-6 6"/></svg>',
        help: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 14.5a2 2 0 0 1-2 2H9l-4 3.5V6a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2z"/></svg>',
        chev: '<svg class="ais-ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9 5l7 7-7 7"/></svg>'
    };

    // --------------------------------------------------------------- styles ---

    var ACCENT = '#00a4dc';

    var CSS =
    '.ais-fab{position:fixed;right:24px;bottom:24px;z-index:1000;width:48px;height:48px;cursor:pointer;' +
      'border:1px solid rgba(255,255,255,.14);color:' + ACCENT + ';background:#15171b;' +
      'display:flex;align-items:center;justify-content:center;transition:border-color .15s,background .15s,transform .12s}' +
    '.ais-fab:hover{border-color:' + ACCENT + ';background:#191c21;transform:translateY(-1px)}' +
    '.ais-fab .ais-ic{width:22px;height:22px}' +

    '.ais-overlay{position:fixed;inset:0;z-index:1001;background:rgba(0,0,0,.58);' +
      'display:flex;align-items:flex-start;justify-content:center;padding:9vh 16px 24px}' +

    '.ais-panel{--acc:' + ACCENT + ';width:100%;max-width:580px;max-height:82vh;display:flex;flex-direction:column;position:relative;' +
      'color:#e9ecf0;-webkit-font-smoothing:antialiased;background:#15171b;' +
      'border:1px solid rgba(255,255,255,.13);' +
      'box-shadow:0 32px 64px -20px rgba(0,0,0,.85),inset 0 1px 0 rgba(255,255,255,.05);' +
      'overflow:hidden;animation:ais-in .16s ease-out}' +

    '.ais-head{display:flex;align-items:center;gap:11px;padding:14px 15px;border-bottom:1px solid rgba(255,255,255,.09)}' +
    '.ais-brand{display:flex;align-items:center;gap:10px}' +
    '.ais-mark{width:26px;height:26px;display:flex;align-items:center;justify-content:center;color:' + ACCENT + ';' +
      'background:rgba(0,164,220,.13);border:1px solid rgba(0,164,220,.3)}' +
    '.ais-mark .ais-ic{width:15px;height:15px}' +
    '.ais-ttl{font-size:14.5px;font-weight:600;letter-spacing:-.01em;color:#f2f4f6}' +
    '.ais-spacer{flex:1}' +
    '.ais-icbtn{width:28px;height:28px;border:1px solid transparent;cursor:pointer;background:transparent;color:#868d97;' +
      'display:flex;align-items:center;justify-content:center;transition:background .13s,color .13s,border-color .13s}' +
    '.ais-icbtn:hover{background:rgba(255,255,255,.06);border-color:rgba(255,255,255,.1);color:#eef1f4}' +
    '.ais-icbtn .ais-ic{width:17px;height:17px}' +

    '.ais-body{padding:15px;overflow-y:auto;overscroll-behavior:contain;display:flex;flex-direction:column;gap:15px}' +
    '.ais-body::-webkit-scrollbar{width:10px}' +
    '.ais-body::-webkit-scrollbar-thumb{background:rgba(255,255,255,.1);border:3px solid transparent;background-clip:content-box}' +
    '.ais-composer{min-height:42px;flex:0 0 auto}' +
    '.ais-content{flex:0 0 auto}' +

    '.ais-actions{display:flex;gap:8px}' +
    '.ais-pill{flex:1;min-width:0;height:42px;border:1px solid rgba(255,255,255,.13);background:transparent;' +
      'color:#cbd0d6;font-size:13.5px;font-weight:500;letter-spacing:-.01em;display:flex;align-items:center;justify-content:center;gap:8px;cursor:pointer;' +
      'transition:background .14s,border-color .14s,color .14s}' +
    '.ais-pill:hover{background:rgba(255,255,255,.05);border-color:rgba(255,255,255,.22);color:#eef1f4}' +
    '.ais-pill:active{background:rgba(255,255,255,.08)}' +
    '.ais-pill .ais-ic{width:17px;height:17px;opacity:.72}' +
    '.ais-pill.primary{background:' + ACCENT + ';border-color:' + ACCENT + ';color:#fff;font-weight:600}' +
    '.ais-pill.primary:hover{background:#0fb0e8;border-color:#0fb0e8;color:#fff}' +
    '.ais-pill.primary .ais-ic{opacity:1}' +
    '.ais-pill .lbl{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +

    '.ais-inputwrap{display:flex;align-items:center;gap:8px;min-height:44px;padding:5px 5px 5px 14px;background:transparent;' +
      'border:1px solid rgba(255,255,255,.16);transform-origin:left center;animation:ais-expand .22s ease-out}' +
    '.ais-inputwrap:focus-within{border-color:' + ACCENT + '}' +
    '.ais-badge2{font-size:11px;font-weight:600;color:' + ACCENT + ';background:rgba(0,164,220,.13);border:1px solid rgba(0,164,220,.3);padding:3px 8px;white-space:nowrap;align-self:center}' +
    '.ais-input{flex:1;min-width:0;background:transparent;border:none;outline:none;color:#f2f4f6;font-size:14.5px;letter-spacing:-.01em;' +
      'font-family:inherit;line-height:20px;height:20px;max-height:40px;padding:0;margin:0;resize:none;overflow-y:auto;overscroll-behavior:contain;display:block}' +
    '.ais-input::placeholder{color:#6a707a}' +
    '.ais-help{height:32px;flex:none;padding:0 12px;border:1px solid rgba(255,255,255,.14);cursor:pointer;background:transparent;' +
      'color:#c2c8cf;font-size:12.5px;font-weight:500;display:flex;align-items:center;gap:6px;white-space:nowrap;transition:background .14s,border-color .14s,color .14s}' +
    '.ais-help:hover{background:rgba(255,255,255,.05);border-color:rgba(255,255,255,.22);color:#eef1f4}' +
    '.ais-help .ais-ic{width:15px;height:15px;opacity:.7}' +
    '.ais-submit{width:34px;height:34px;flex:none;border:1px solid ' + ACCENT + ';cursor:pointer;color:#fff;background:' + ACCENT + ';' +
      'display:flex;align-items:center;justify-content:center;transition:background .14s}' +
    '.ais-submit:hover{background:#0fb0e8}' +
    '.ais-submit .ais-ic{width:17px;height:17px}' +

    '.ais-prefs{display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap;padding:9px 1px 0}' +
    '.ais-scopebar{display:flex;margin-bottom:11px}' +
    '.ais-seg{display:inline-flex;border:1px solid rgba(255,255,255,.13)}' +
    '.ais-segbtn{background:transparent;border:none;cursor:pointer;color:#868d97;font-size:12px;font-weight:500;letter-spacing:-.01em;padding:6px 14px;transition:background .14s,color .14s}' +
    '.ais-segbtn + .ais-segbtn{border-left:1px solid rgba(255,255,255,.13)}' +
    '.ais-segbtn:hover{color:#dfe3e8}' +
    '.ais-segbtn.sel{background:rgba(0,164,220,.16);color:#84d6f2}' +
    '.ais-pref{display:inline-flex;align-items:center;gap:8px;background:none;border:none;cursor:pointer;color:#767c86;font-size:11.5px;letter-spacing:-.01em;padding:2px}' +
    '.ais-pref:hover{color:#aeb4bd}' +
    '.ais-switch{position:relative;width:28px;height:15px;flex:none;background:rgba(255,255,255,.11);transition:background .16s}' +
    '.ais-switch::after{content:"";position:absolute;top:2px;left:2px;width:11px;height:11px;background:#8a9099;transition:transform .16s,background .16s}' +
    '.ais-pref[data-on="1"] .ais-switch{background:rgba(0,164,220,.28)}' +
    '.ais-pref[data-on="1"] .ais-switch::after{transform:translateX(13px);background:' + ACCENT + '}' +
    '.ais-pref[data-on="1"]{color:#aeb4bd}' +

    '.ais-sectlbl{font-size:10.5px;font-weight:600;letter-spacing:.11em;text-transform:uppercase;color:#5b616b;margin:0 1px 7px}' +

    '.ais-list{display:flex;flex-direction:column;border:1px solid rgba(255,255,255,.1)}' +
    '.ais-row{display:flex;align-items:center;gap:11px;padding:7px 11px;cursor:pointer;transition:background .13s}' +
    '.ais-row + .ais-row{border-top:1px solid rgba(255,255,255,.07)}' +
    '.ais-row:hover{background:rgba(255,255,255,.045)}' +
    '.ais-row:hover .ais-chev{color:#9098a2;transform:translateX(2px)}' +
    '.ais-thumbs{display:flex;flex:none}' +
    '.ais-thumbs img,.ais-thumbs .ph{width:25px;height:37px;object-fit:cover;background:#20242b;' +
      'box-shadow:0 0 0 1px rgba(0,0,0,.6);margin-left:-13px}' +
    '.ais-thumbs img:first-child,.ais-thumbs .ph:first-child{margin-left:0}' +
    '.ais-exmark{width:26px;height:26px;flex:none;display:flex;align-items:center;justify-content:center;background:rgba(255,255,255,.05);color:#8a9099}' +
    '.ais-exmark .ais-ic{width:15px;height:15px}' +
    '.ais-rowtxt{flex:1;min-width:0}' +
    '.ais-rowq{font-size:13px;font-weight:500;letter-spacing:-.01em;color:#e9ecf0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.ais-rowmeta{font-size:11px;color:#6a707a;margin-top:1px;display:flex;align-items:center;gap:5px}' +
    '.ais-rowmeta .ais-ic{width:11px;height:11px}' +
    '.ais-chev{flex:none;color:#4a505a;display:flex;transition:color .13s,transform .13s}' +
    '.ais-chev .ais-ic{width:15px;height:15px}' +
    '.ais-clear{background:none;border:none;color:#565c66;font-size:11.5px;cursor:pointer;padding:5px 3px;transition:color .14s}' +
    '.ais-clear:hover{color:#c2c8cf}' +

    '.ais-empty{padding:6px 2px 0}' +
    '.ais-empty h4{margin:0 0 4px;font-size:14.5px;font-weight:600;letter-spacing:-.01em;color:#eef1f4}' +
    '.ais-empty p{margin:0;font-size:12.5px;line-height:1.5;color:#7c828c;max-width:42ch}' +

    '.ais-loading{display:flex;align-items:center;gap:12px;padding:20px 3px}' +
    '.ais-spin{width:17px;height:17px;flex:none;border:2px solid rgba(255,255,255,.14);border-top-color:' + ACCENT + ';border-radius:50%;animation:ais-spin .7s linear infinite}' +
    '.ais-loadtxt{font-size:13.5px;letter-spacing:-.01em;color:#c6ccd3}' +

    '.ais-answer{font-size:14px;line-height:1.55;letter-spacing:-.01em;color:#dde1e7;margin:0 1px}' +
    '.ais-savebar{display:flex;align-items:center;gap:8px;height:42px;padding:0 5px 0 13px;background:transparent;border:1px solid rgba(255,255,255,.16)}' +
    '.ais-savebar input{flex:1;min-width:0;background:transparent;border:none;outline:none;color:#f2f4f6;font-size:14px;letter-spacing:-.01em}' +
    '.ais-savebar input::placeholder{color:#6a707a}' +
    '.ais-savebtn{height:32px;padding:0 14px;border:1px solid ' + ACCENT + ';cursor:pointer;color:#fff;font-size:12.5px;font-weight:600;' +
      'background:' + ACCENT + ';display:flex;align-items:center;gap:6px;white-space:nowrap;transition:background .14s}' +
    '.ais-savebtn:hover{background:#0fb0e8}.ais-savebtn:disabled{opacity:.55;cursor:default}' +
    '.ais-savebtn .ais-ic{width:15px;height:15px}' +

    '.ais-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(108px,1fr));gap:15px}' +
    '.ais-card{cursor:pointer}' +
    '.ais-poster{width:100%;aspect-ratio:2/3;object-fit:cover;display:block;background:#20242b;' +
      'box-shadow:0 0 0 1px rgba(255,255,255,.07);transition:box-shadow .15s,transform .15s}' +
    '.ais-card:hover .ais-poster{transform:translateY(-3px);box-shadow:0 0 0 1px ' + ACCENT + '}' +
    '.ais-cardname{font-size:12.5px;font-weight:600;letter-spacing:-.01em;margin-top:8px;color:#eef1f4;line-height:1.25}' +
    '.ais-cardyear{font-size:11.5px;color:#6a707a;margin-top:1px}' +
    '.ais-cardreason{font-size:11.5px;color:#9aa0a9;margin-top:5px;line-height:1.42}' +

    '.ais-footer{display:flex;flex-wrap:wrap;justify-content:center;gap:8px;padding:2px 0}' +
    '.ais-ghost{height:34px;padding:0 16px;border:1px solid rgba(255,255,255,.14);cursor:pointer;background:transparent;' +
      'color:#c6ccd3;font-size:12.5px;font-weight:500;display:inline-flex;align-items:center;gap:7px;transition:background .14s,border-color .14s,color .14s}' +
    '.ais-ghost:hover{background:rgba(255,255,255,.05);border-color:rgba(255,255,255,.22);color:#eef1f4}' +
    '.ais-ghost .ais-ic{width:15px;height:15px;opacity:.8}' +

    '.ais-quiz{display:flex;flex-direction:column;gap:15px}' +
    '.ais-refine{font-size:11px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:#7d8794;margin:0 1px}' +
    '.ais-preparing{display:flex;align-items:center;gap:11px;padding:14px 3px;font-size:13.5px;color:#c6ccd3}' +
    '.ais-q label{display:block;font-size:13px;font-weight:600;letter-spacing:-.01em;color:#e2e6eb;margin:0 1px 8px}' +
    '.ais-chips{display:flex;flex-wrap:wrap;gap:7px}' +
    '.ais-chip{padding:7px 13px;border:1px solid rgba(255,255,255,.14);background:transparent;' +
      'color:#c6ccd3;font-size:12.5px;cursor:pointer;transition:background .14s,border-color .14s,color .14s}' +
    '.ais-chip:hover{background:rgba(255,255,255,.05);border-color:rgba(255,255,255,.22)}' +
    '.ais-chip.sel{background:rgba(0,164,220,.16);border-color:' + ACCENT + ';color:#84d6f2}' +

    '.ais-err{color:#f08a8a;font-size:13.5px;padding:14px 1px}' +
    '.ais-toast{position:absolute;left:50%;bottom:15px;transform:translateX(-50%);background:#1c1f24;' +
      'border:1px solid rgba(255,255,255,.14);color:#eef1f4;font-size:12.5px;font-weight:500;padding:9px 14px;' +
      'display:flex;align-items:center;gap:8px;box-shadow:0 12px 30px -12px rgba(0,0,0,.8);z-index:3}' +
    '.ais-toast .ais-ic{width:14px;height:14px;color:' + ACCENT + '}' +

    '@keyframes ais-in{from{transform:translateY(10px)}to{transform:none}}' +
    '@keyframes ais-expand{from{transform:scaleX(.72)}to{transform:scaleX(1)}}' +
    '@keyframes ais-spin{to{transform:rotate(360deg)}}' +
    '@media (max-width:640px){.ais-helptxt{display:none}.ais-help{padding:0 9px}}' +
    '@media (prefers-reduced-motion:reduce){.ais-panel,.ais-inputwrap{animation:none!important}.ais-spin{animation-duration:1.2s}}';

    function injectStyles() {
        if (document.getElementById('ais-styles')) { return; }
        var s = document.createElement('style');
        s.id = 'ais-styles';
        s.textContent = CSS;
        document.head.appendChild(s);
    }

    // ----------------------------------------------------------- rendering ---

    var overlay = null;
    var els = {};
    var lastRun = null;
    var loadTimer = null;
    var historyCache = [];

    function cacheHistory(list) { historyCache = list || []; }

    // User preferences (persisted, best-effort — some webviews block storage).
    var prefs = { personalize: readPref('aisPersonalize', true), scope: readStr('aisScope', 'movies') };
    function readPref(key, def) {
        try { var v = window.localStorage.getItem(key); return v === null ? def : v === '1'; } catch (e) { return def; }
    }
    function writePref(key, on) {
        try { window.localStorage.setItem(key, on ? '1' : '0'); } catch (e) { }
    }
    function readStr(key, def) {
        try { var v = window.localStorage.getItem(key); return v === null ? def : v; } catch (e) { return def; }
    }
    function writeStr(key, val) {
        try { window.localStorage.setItem(key, val); } catch (e) { }
    }
    function scopeHtml() {
        var s = t();
        return '<div class="ais-seg" role="tablist">' +
            '<button class="ais-segbtn' + (prefs.scope === 'movies' ? ' sel' : '') + '" data-scope="movies">' + esc(s.scopeMovies) + '</button>' +
            '<button class="ais-segbtn' + (prefs.scope === 'tv' ? ' sel' : '') + '" data-scope="tv">' + esc(s.scopeTv) + '</button>' +
            '</div>';
    }
    function wireScope(root) {
        root.querySelectorAll('.ais-segbtn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                prefs.scope = btn.getAttribute('data-scope');
                writeStr('aisScope', prefs.scope);
                root.querySelectorAll('.ais-segbtn').forEach(function (b) { b.classList.toggle('sel', b === btn); });
            });
        });
    }
    function toggleHtml(key, label, on) {
        return '<button class="ais-pref" data-tgl="' + key + '" data-on="' + (on ? '1' : '0') + '" aria-pressed="' + (on ? 'true' : 'false') + '">' +
            '<span class="ais-switch"></span><span>' + esc(label) + '</span></button>';
    }
    function wireToggle(root, key, onChange) {
        var btn = root.querySelector('.ais-pref[data-tgl="' + key + '"]');
        if (!btn) { return; }
        btn.addEventListener('click', function () {
            var on = btn.getAttribute('data-on') !== '1';
            btn.setAttribute('data-on', on ? '1' : '0');
            btn.setAttribute('aria-pressed', on ? 'true' : 'false');
            onChange(on);
        });
    }

    function frag(html) { var d = document.createElement('div'); d.innerHTML = html; return d; }

    function closePanel() {
        stopLoader();
        if (overlay && overlay.parentNode) { overlay.parentNode.removeChild(overlay); }
        overlay = null; els = {};
        document.removeEventListener('keydown', onKey);
        document.documentElement.style.overflow = prevHtmlOverflow;
    }

    var prevHtmlOverflow = '';

    function onKey(e) { if (overlay && e.key === 'Escape') { closePanel(); } }

    function openPanel() {
        if (overlay) { return; }
        prevHtmlOverflow = document.documentElement.style.overflow;
        document.documentElement.style.overflow = 'hidden';
        var s = t();
        overlay = document.createElement('div');
        overlay.className = 'ais-overlay';
        overlay.innerHTML =
            '<div class="ais-panel" role="dialog" aria-label="' + esc(s.title) + '">' +
            '<div class="ais-head">' +
            '<button class="ais-icbtn ais-back" title="' + esc(s.back) + '" style="display:none">' + IC.back + '</button>' +
            '<div class="ais-brand"><span class="ais-mark">' + IC.sparkle + '</span><span class="ais-ttl">' + esc(s.title) + '</span></div>' +
            '<span class="ais-spacer"></span>' +
            '<button class="ais-icbtn ais-close" title="' + esc(s.close) + '">' + IC.close + '</button>' +
            '</div>' +
            '<div class="ais-body"><div class="ais-composer"></div><div class="ais-content"></div></div>' +
            '</div>';
        document.body.appendChild(overlay);

        els.panel = overlay.querySelector('.ais-panel');
        els.back = overlay.querySelector('.ais-back');
        els.composer = overlay.querySelector('.ais-composer');
        els.content = overlay.querySelector('.ais-content');

        overlay.addEventListener('click', function (e) { if (e.target === overlay) { closePanel(); } });
        els.panel.addEventListener('click', function (e) { e.stopPropagation(); });
        overlay.querySelector('.ais-close').addEventListener('click', closePanel);
        els.back.addEventListener('click', goHome);
        document.addEventListener('keydown', onKey);

        goHome();
    }

    function showBack(on) { els.back.style.display = on ? 'flex' : 'none'; }

    function goHome() { showBack(false); setActions(); renderHistory(); }

    function setActions() {
        var s = t();
        els.composer.innerHTML =
            '<div class="ais-scopebar">' + scopeHtml() + '</div>' +
            '<div class="ais-actions ais-anim-actions">' +
            '<button class="ais-pill primary" data-a="search">' + IC.sparkle + '<span class="lbl">' + esc(s.search) + '</span></button>' +
            '<button class="ais-pill" data-a="surprise">' + IC.dice + '<span class="lbl">' + esc(s.surprise) + '</span></button>' +
            '<button class="ais-pill" data-a="collection">' + IC.layers + '<span class="lbl">' + esc(s.collection) + '</span></button>' +
            '</div>';
        wireScope(els.composer);
        els.composer.querySelector('[data-a="search"]').addEventListener('click', function () { setInput(false); });
        els.composer.querySelector('[data-a="surprise"]').addEventListener('click', doSurprise);
        els.composer.querySelector('[data-a="collection"]').addEventListener('click', function () { setInput(true); });
    }

    // Grows the textarea with its content up to two lines, then it scrolls.
    function autoGrow(el) { el.style.height = 'auto'; el.style.height = Math.min(el.scrollHeight, 40) + 'px'; }

    // Builds the compact input bar. Reused when collapsing the interview quiz
    // back down (value prefilled, focus suppressed) so results never overlap it.
    function renderInputBar(collection, value, focus) {
        var s = t();
        els.composer.innerHTML =
            '<div class="ais-inputwrap">' +
            (collection ? '<span class="ais-badge2">' + esc(s.collection) + '</span>' : '') +
            '<textarea class="ais-input" rows="1" placeholder="' + esc(collection ? s.collectionPlaceholder : s.placeholder) + '"></textarea>' +
            (collection ? '' : '<button class="ais-help" data-a="help">' + IC.help + '<span class="ais-helptxt">' + esc(s.helpMe) + '</span></button>') +
            '<button class="ais-submit" data-a="submit" aria-label="' + esc(s.search) + '">' + IC.send + '</button>' +
            '</div>' +
            '<div class="ais-prefs">' + scopeHtml() + toggleHtml('personalize', s.personalize, prefs.personalize) + '</div>';
        var input = els.composer.querySelector('.ais-input');
        if (value) { input.value = value; }
        var submit = function () { var q = input.value.trim(); if (q) { runSearch(q, { collection: collection }); } };
        els.composer.querySelector('[data-a="submit"]').addEventListener('click', submit);
        wireScope(els.composer);
        wireToggle(els.composer, 'personalize', function (on) { prefs.personalize = on; writePref('aisPersonalize', on); });
        var help = els.composer.querySelector('[data-a="help"]');
        if (help) { help.addEventListener('click', setInterview); }
        input.addEventListener('input', function () { autoGrow(input); });
        input.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); } });
        autoGrow(input);
        if (focus !== false) { setTimeout(function () { input.focus(); }, 60); }
        return input;
    }

    function setInput(collection) { showBack(true); renderInputBar(collection, '', true); }

    // "Help me choose": if the user already typed something, ask the model for
    // questions tailored to it; otherwise fall back to the generic quiz.
    function setInterview() {
        var input = els.composer.querySelector('.ais-input');
        var initial = input ? input.value.trim() : '';
        showBack(true);
        if (!initial) { renderStaticInterview(); return; }
        var s = t();
        els.composer.innerHTML =
            '<div class="ais-preparing"><div class="ais-spin"></div><span>' + esc(s.preparing) + '…</span></div>';
        getInterview(initial)
            .then(function (questions) {
                if (!questions || !questions.length) { renderStaticInterview(); return; }
                renderDynamicInterview(initial, questions);
            })
            .catch(function () { renderStaticInterview(); });
    }

    // Wires single-select chips (one pick per data-k group) writing into `answers`.
    function wireQuiz(answers) {
        els.composer.querySelectorAll('.ais-chip').forEach(function (chip) {
            chip.addEventListener('click', function () {
                var k = chip.getAttribute('data-k');
                els.composer.querySelectorAll('.ais-chip[data-k="' + k + '"]').forEach(function (c) { c.classList.remove('sel'); });
                chip.classList.add('sel');
                answers[k] = chip.getAttribute('data-v');
            });
        });
    }

    function renderStaticInterview() {
        var s = t();
        var answers = { mood: null, time: null, seen: null };
        function group(key, label, opts) {
            var chips = opts.map(function (o) { return '<button class="ais-chip" data-k="' + key + '" data-v="' + o[0] + '">' + esc(o[1]) + '</button>'; }).join('');
            return '<div class="ais-q"><label>' + esc(label) + '</label><div class="ais-chips">' + chips + '</div></div>';
        }
        els.composer.innerHTML =
            '<div class="ais-quiz">' +
            group('mood', s.q1, s.q1opts) + group('time', s.q2, s.q2opts) + group('seen', s.q3, s.q3opts) +
            '<div><button class="ais-savebtn" data-a="go" style="height:40px;padding:0 18px">' + IC.sparkle + esc(s.go) + '</button></div>' +
            '</div>';
        wireQuiz(answers);
        els.composer.querySelector('[data-a="go"]').addEventListener('click', function () {
            var prompt = interviewPrompt(answers);
            renderInputBar(false, prompt, false);
            runSearch(prompt, { includeWatched: answers.seen === 'rewatch' });
        });
    }

    function renderDynamicInterview(initial, questions) {
        var s = t();
        var answers = {};
        var groups = questions.map(function (q, qi) {
            var chips = (q.options || []).map(function (opt) {
                return '<button class="ais-chip" data-k="q' + qi + '" data-v="' + esc(opt) + '">' + esc(opt) + '</button>';
            }).join('');
            return '<div class="ais-q"><label>' + esc(q.label) + '</label><div class="ais-chips">' + chips + '</div></div>';
        }).join('');
        els.composer.innerHTML =
            '<div class="ais-quiz"><div class="ais-refine">' + esc(s.refine) + '</div>' + groups +
            '<div><button class="ais-savebtn" data-a="go" style="height:40px;padding:0 18px">' + IC.sparkle + esc(s.go) + '</button></div>' +
            '</div>';
        wireQuiz(answers);
        els.composer.querySelector('[data-a="go"]').addEventListener('click', function () {
            var picks = Object.keys(answers).map(function (k) { return answers[k]; }).filter(Boolean);
            var prompt = initial;
            if (picks.length) { prompt += (isFrench() ? '. Préférences : ' : '. Preferences: ') + picks.join(', ') + '.'; }
            renderInputBar(false, prompt, false);
            runSearch(prompt, {});
        });
    }

    function doSurprise() { showBack(true); setActions(); runSearch('', { server: 'surprise' }); }

    // ------------------------------------------------------------- loader ---

    function startLoader(surprise) {
        var s = t();
        var steps = surprise ? [s.surprising] : s.loading.slice();
        els.content.innerHTML = '<div class="ais-loading"><div class="ais-spin"></div><div class="ais-loadtxt">' + esc(steps[0]) + '…</div></div>';
        var txt = els.content.querySelector('.ais-loadtxt');
        var i = 0;
        stopLoader();
        if (steps.length > 1) {
            loadTimer = setInterval(function () {
                i = (i + 1) % steps.length;
                txt.textContent = steps[i] + '…';
            }, 1400);
        }
    }

    function stopLoader() { if (loadTimer) { clearInterval(loadTimer); loadTimer = null; } }

    // ------------------------------------------------------------- search ---

    function runSearch(prompt, opts) {
        opts = opts || {};
        showBack(true);
        startLoader(opts.server === 'surprise');
        recommend({ prompt: prompt, server: opts.server || 'normal', includeWatched: opts.includeWatched, excludeItemIds: opts.excludeItemIds })
            .then(function (data) {
                stopLoader();
                lastRun = { prompt: prompt, server: opts.server || 'normal', collection: !!opts.collection, includeWatched: opts.includeWatched, results: (data && data.results) || [] };
                renderResults(data, !!opts.collection);
                loadHistory().then(cacheHistory);
            })
            .catch(function (err) {
                stopLoader();
                var s = t();
                var msg = s.unavailable;
                if (err && err.status === 503) { msg = s.disabled; }
                else if (err && err.status === 504) { msg = s.timeout; }
                els.content.innerHTML = '<div class="ais-err">' + esc(msg) + '</div>';
            });
    }

    function cardHtml(r, i) {
        var img = imageUrl(r.itemId);
        return '<div class="ais-card" data-id="' + esc(r.itemId) + '" style="animation-delay:' + (i * 35) + 'ms">' +
            (img ? '<img class="ais-poster" loading="lazy" src="' + esc(img) + '" onerror="this.style.visibility=\'hidden\'" />' : '<div class="ais-poster"></div>') +
            '<div class="ais-cardname">' + esc(r.title) + '</div>' +
            (r.year ? '<div class="ais-cardyear">' + esc(r.year) + '</div>' : '') +
            (r.reason ? '<div class="ais-cardreason">' + esc(r.reason) + '</div>' : '') +
            '</div>';
    }

    function wireCards(root) {
        root.querySelectorAll('.ais-card').forEach(function (card) {
            card.addEventListener('click', function () { closePanel(); navigate(card.getAttribute('data-id')); });
        });
    }

    function defaultCollName() {
        var n = (lastRun && lastRun.prompt ? lastRun.prompt : 'AI collection').replace(/\s+/g, ' ').trim();
        return n ? n.charAt(0).toUpperCase() + n.slice(1) : 'AI collection';
    }

    function saveBarHtml() {
        var s = t();
        return '<div class="ais-savebar"><input class="ais-savein" type="text" value="' + esc(defaultCollName()) + '" placeholder="' + esc(s.savePlaceholder) + '" />' +
            '<button class="ais-savebtn" data-a="save">' + IC.layers + esc(s.save) + '</button></div>';
    }

    function wireSaveBar(results) {
        var saveBtn = els.content.querySelector('.ais-savebar [data-a="save"]');
        if (!saveBtn) { return; }
        saveBtn.addEventListener('click', function () {
            var s = t();
            var name = (els.content.querySelector('.ais-savein').value || '').trim() || 'AI collection';
            var ids = results.map(function (r) { return r.itemId; });
            saveBtn.disabled = true; saveBtn.innerHTML = IC.layers + esc(s.saving);
            createPlaylist(name, ids)
                .then(function () { toast(s.saved(name)); saveBtn.innerHTML = IC.layers + esc(s.saved(name)); })
                .catch(function () { saveBtn.disabled = false; saveBtn.innerHTML = IC.layers + esc(s.save); toast(s.saveFailed); });
        });
    }

    // Reveals the name-it-and-save bar at the top of the results on demand
    // (the "Save to playlist" footer button), or focuses it if already shown.
    function revealSaveBar(results) {
        var existing = els.content.querySelector('.ais-savebar');
        if (!existing) {
            existing = frag(saveBarHtml()).firstChild;
            els.content.insertBefore(existing, els.content.firstChild);
            wireSaveBar(results);
        }
        var inp = existing.querySelector('.ais-savein');
        if (inp) { inp.focus(); inp.select(); }
        existing.scrollIntoView({ block: 'nearest' });
    }

    function renderResults(data, collection) {
        var s = t();
        var results = (data && data.results) || [];
        if (!results.length) { els.content.innerHTML = '<div class="ais-err">' + esc(s.noMatches) + '</div>'; return; }
        var html = '';
        if (collection) { html += saveBarHtml(); }
        if (data && data.answer) { html += '<div class="ais-answer">' + esc(data.answer) + '</div>'; }
        html += '<div class="ais-grid">' + results.map(cardHtml).join('') + '</div>';
        html += '<div class="ais-footer">' +
            '<button class="ais-ghost" data-a="more">' + IC.sparkle + esc(s.more) + '</button>' +
            (collection ? '' : '<button class="ais-ghost" data-a="savetoggle">' + IC.layers + esc(s.saveResults) + '</button>') +
            '</div>';
        els.content.innerHTML = html;
        wireCards(els.content);
        if (collection) { wireSaveBar(results); }

        var saveToggle = els.content.querySelector('[data-a="savetoggle"]');
        if (saveToggle) { saveToggle.addEventListener('click', function () { revealSaveBar(results); }); }

        var moreBtn = els.content.querySelector('[data-a="more"]');
        if (moreBtn) {
            moreBtn.addEventListener('click', function () {
                var shown = Array.prototype.map.call(els.content.querySelectorAll('.ais-card'), function (c) { return c.getAttribute('data-id'); });
                moreBtn.disabled = true; moreBtn.textContent = '…';
                recommend({ prompt: lastRun.prompt, server: lastRun.server, includeWatched: lastRun.includeWatched, excludeItemIds: shown })
                    .then(function (d) {
                        var more = (d && d.results) || [];
                        if (!more.length) { if (moreBtn.parentNode) { moreBtn.parentNode.removeChild(moreBtn); } return; }
                        var grid = els.content.querySelector('.ais-grid');
                        var start = grid.children.length;
                        var block = frag(more.map(function (r, k) { return cardHtml(r, start + k); }).join(''));
                        while (block.firstChild) { grid.appendChild(block.firstChild); }
                        wireCards(grid);
                        results.push.apply(results, more);
                        moreBtn.disabled = false; moreBtn.innerHTML = IC.sparkle + esc(t().more);
                    })
                    .catch(function () { moreBtn.disabled = false; moreBtn.innerHTML = IC.sparkle + esc(t().more); });
            });
        }
    }

    // ------------------------------------------------------------ history ---

    function renderHistory() {
        var s = t();
        loadHistory().then(function (list) {
            cacheHistory(list);
            if (!list.length) {
                var ex = s.examples.map(function (q) {
                    return '<div class="ais-row" data-ex="' + esc(q) + '"><span class="ais-exmark">' + IC.wand + '</span><div class="ais-rowtxt"><div class="ais-rowq">' + esc(q) + '</div></div><span class="ais-chev">' + IC.chev + '</span></div>';
                }).join('');
                els.content.innerHTML = '<div class="ais-empty"><h4>' + esc(s.emptyTitle) + '</h4><p>' + esc(s.emptyBody) + '</p></div><div class="ais-list">' + ex + '</div>';
                els.content.querySelectorAll('[data-ex]').forEach(function (row) {
                    row.addEventListener('click', function () { runSearch(row.getAttribute('data-ex'), {}); });
                });
                return;
            }
            var rows = list.map(function (h) {
                var thumbs = (h.items || []).slice(0, 3).map(function (it) {
                    var img = imageUrl(it.itemId);
                    return img ? '<img loading="lazy" src="' + esc(img) + '" onerror="this.style.visibility=\'hidden\'" />' : '<span class="ph"></span>';
                }).join('');
                var label = h.prompt && h.prompt.trim() ? h.prompt : (h.mode === 'surprise' ? s.surprise : s.title);
                var icon = h.mode === 'surprise' ? IC.dice : IC.clock;
                return '<div class="ais-row" data-id="' + esc(h.id) + '">' +
                    '<div class="ais-thumbs">' + thumbs + '</div>' +
                    '<div class="ais-rowtxt"><div class="ais-rowq">' + esc(label) + '</div>' +
                    '<div class="ais-rowmeta">' + icon + '<span>' + esc(s.movies(h.count || (h.items || []).length)) + ' · ' + esc(relTime(h.at)) + '</span></div></div>' +
                    '<span class="ais-chev">' + IC.chev + '</span>' +
                    '</div>';
            }).join('');
            els.content.innerHTML = '<div><div class="ais-sectlbl">' + esc(s.recent) + '</div><div class="ais-list">' + rows + '</div>' +
                '<div class="ais-footer" style="justify-content:flex-end;padding-top:6px"><button class="ais-clear" data-a="clear">' + esc(s.clear) + '</button></div></div>';
            els.content.querySelectorAll('.ais-row').forEach(function (row) {
                row.addEventListener('click', function () { openHistory(row.getAttribute('data-id')); });
            });
            var clr = els.content.querySelector('[data-a="clear"]');
            if (clr) { clr.addEventListener('click', function () { deleteHistory(null).then(renderHistory); }); }
        });
    }

    function openHistory(id) {
        var s = t();
        var h = historyCache.filter(function (x) { return x.id === id; })[0];
        if (!h) { return; }
        showBack(true);
        lastRun = { prompt: h.prompt, server: h.mode === 'surprise' ? 'surprise' : 'normal', collection: false, results: h.items || [] };
        var items = h.items || [];
        var html = (h.prompt ? '<div class="ais-answer">' + esc(h.prompt) + '</div>' : '') +
            '<div class="ais-grid">' + items.map(cardHtml).join('') + '</div>' +
            '<div class="ais-footer"><button class="ais-ghost" data-a="again">' + IC.sparkle + esc(s.searchAgain) + '</button>' +
            '<button class="ais-ghost" data-a="savetoggle">' + IC.layers + esc(s.saveResults) + '</button></div>';
        els.content.innerHTML = html;
        wireCards(els.content);
        els.content.querySelector('[data-a="again"]').addEventListener('click', function () {
            runSearch(h.prompt, { server: h.mode === 'surprise' ? 'surprise' : 'normal' });
        });
        els.content.querySelector('[data-a="savetoggle"]').addEventListener('click', function () { revealSaveBar(items); });
    }

    // -------------------------------------------------------------- toast ---

    function toast(msg) {
        if (!els.panel) { return; }
        var old = els.panel.querySelector('.ais-toast');
        if (old) { old.parentNode.removeChild(old); }
        var el = frag('<div class="ais-toast">' + IC.sparkle + '<span>' + esc(msg) + '</span></div>').firstChild;
        els.panel.appendChild(el);
        setTimeout(function () { if (el.parentNode) { el.parentNode.removeChild(el); } }, 3200);
    }

    // ------------------------------------------------------------ launcher ---

    var fab = null;
    var usable = false;

    function isPlaying() {
        var hash = (window.location.hash || '').toLowerCase();
        if (hash.indexOf('/video') !== -1) { return true; }
        var players = document.querySelectorAll('.videoPlayerContainer:not(.hide)');
        for (var i = 0; i < players.length; i++) {
            if (players[i] && players[i].offsetParent !== null) { return true; }
        }
        return false;
    }

    function ensureFab() {
        if (fab && fab.parentNode) { return; }
        fab = document.createElement('button');
        fab.className = 'ais-fab';
        fab.title = t().fabTitle;
        fab.setAttribute('aria-label', t().fabTitle);
        fab.innerHTML = IC.sparkle;
        fab.addEventListener('click', openPanel);
        document.body.appendChild(fab);
    }

    function removeFab() {
        if (fab && fab.parentNode) { fab.parentNode.removeChild(fab); }
        fab = null;
    }

    function updateLauncher() {
        if (usable && !isPlaying()) { ensureFab(); }
        else { removeFab(); if (!usable) { closePanel(); } }
    }

    function refreshHealth() {
        var a = api();
        if (!a || typeof a.ajax !== 'function' || typeof a.getUrl !== 'function') { return; }
        a.ajax({ type: 'GET', url: a.getUrl('AiSearch/Health'), dataType: 'json' })
            .then(function (h) { usable = !!(h && h.enabled && h.configured); updateLauncher(); })
            .catch(function () { /* keep last known state */ });
    }

    ready(function () {
        injectStyles();
        refreshHealth();
        window.addEventListener('hashchange', updateLauncher);
        setInterval(updateLauncher, 1500);
        setInterval(refreshHealth, 15000);
    });
})();
