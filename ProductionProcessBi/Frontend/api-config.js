// Single-server deployment: the API and the static UI are served by the same origin,
// so the default API base is the page's own origin. This file only sets the base when
// an explicit override has not already been provided.
window.PROCESS_BI_API_BASE = window.PROCESS_BI_API_BASE ?? window.location.origin;
