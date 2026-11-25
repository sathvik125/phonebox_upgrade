# Paused workflows

These GitHub Actions workflows are paused: GitHub only runs workflows in `.github/workflows/`, so nothing here runs.
They came with the original PhoneBox project and were failing on this repository (no GitHub Pages on a private repo,
release and signing secrets that only the original project has, and tests that expect every text translated into all
languages).

To turn one back on, move it back:

```sh
git mv .github/workflows-paused/build.yml .github/workflows/
```
