window.scrollToElement = (id) => {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

window.setTheme = (theme) => {
    document.documentElement.setAttribute('data-theme', theme);
};
