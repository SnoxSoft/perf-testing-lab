# Third-party notices

The [MIT licence](LICENSE) covers only the code in this repository. The load
testing tools it drives carry their own terms, and the difference between them is
a material input to a tooling decision rather than a footnote — see
[docs/tool-comparison.md](docs/tool-comparison.md).

## NBomber

**Proprietary. Free for personal use only.** Organisational use requires a paid
Business or Enterprise licence. Versions 4 and earlier were Apache-2.0; version 5
onward is closed-source, and the runner prints a reminder after every run:

```
THIS VERSION IS FREE ONLY FOR PERSONAL USE. You can't use it for an organization.
```

Read <https://nbomber.com/docs/getting-started/license/> before adopting any of
the NBomber material here at work. This repository is a personal-use project.

## k6

**AGPL-3.0.** Free to self-host, commercially included.
<https://github.com/grafana/k6>

## Neither tool is redistributed here

Both are resolved at build or run time — NBomber from NuGet, k6 from the local
installation or the `grafana/setup-k6-action` step in CI.
