(function () {
  if (window.location.pathname === '/') {
    window.location.replace('/admin/' + window.location.search + window.location.hash);
  }
})();
