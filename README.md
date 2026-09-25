# HLA Imputation Tool

<img width="1536" height="1024" alt="Overview Diagram v1" src="https://github.com/user-attachments/assets/ae805a24-09a7-4ab0-b970-6206fe2cbb5d" />

HLA Imputation Tool is a locally deployable Windows application for haplotype-frequency-based imputation of extended HLA genotypes.

This README separates:

1. **Validated / Published Functionality** — the one-field molecular workflow evaluated in the manuscript.
2. **Additional Functionality Under Development** — broader capabilities implemented in the repository but not covered by the manuscript validation metrics.

> **Research use only**
>
> Imputed HLA genotypes are probabilistic estimates. They are not equivalent to directly observed high-resolution typing and must not replace NGS or another validated typing method.

## Availability

- **Source code:** https://github.com/mae92/HLA-Imputation-Tool
- **Executable and distribution materials:** https://zenodo.org/records/21414456

---

# Validated / Published Functionality

## Validated Workflow

The manuscript evaluated:

| Setting | Validated configuration |
|---|---|
| Input type | Molecular |
| Input resolution | One-field |
| Principal input loci | HLA-A, -B, -C, -DRB1, -DQB1 |
| Matching | `Must Match Input` |
| Iteration | Disabled |
| Validation records | 11,551 |
| Reference result | Two-field NGS typing |

The software returns extended assignments for HLA-A, -B, -C, -DRB1/3/4/5, -DQA1/-DQB1, and -DPA1/-DPB1.

Under `Must Match Input`, a candidate diplotype must reproduce every selected input locus at one-field resolution. If no candidate satisfies all selected loci, the record fails rather than returning a knowingly mismatched genotype.

## Reference Framework

The validated algorithm uses NMDP haplotypes represented in two-field G-group format with frequencies for multiple United States population categories.

When a recorded category is available, the corresponding frequency panel is used. Otherwise, frequencies are averaged across `AFA`, `API`, `CAU`, `HIS`, and `NAM`. If suitable candidates remain unavailable, the highest frequency observed in any panel can be used.

Population-category labels select frequency panels and are not genetically inferred ancestry.

## Validated Input

Use the distributed CSV template. For the principal validated configuration, provide one-field molecular values for:

```text
A
B
C
DRB1
DQB1
```

Examples:

```text
A*01
B*07
C*07
DRB1*15
DQB1*06
```

## Validated Algorithm

1. Parse, clean, and normalize input.
2. Convert supported values to the reference representation.
3. Identify compatible reference haplotypes.
4. Apply the applicable population-frequency strategy.
5. Construct candidate diplotypes.
6. Require one-field agreement at every selected input locus.
7. Rank compatible candidates using:

```text
F_haplotype1 × F_haplotype2 = F_diplotype
```

8. Export the selected genotype and QC information.

The ranking score is not a calibrated probability of correctness.

## Running the Validated Workflow

1. Load the CSV.
2. Select molecular input.
3. Select one-field resolution.
4. Select A, B, C, DRB1, and DQB1.
5. Select `Must Match Input`.
6. Disable iterative searching.
7. Run the imputation.
8. Review transformed input, mismatches, and failed records.
9. Export and retain the workbook.

## Validated QC and Export

The manuscript describes:

1. **Raw Input**
2. **Transformed Input**
3. **Imputed Output**
4. **Mismatch Review**
5. **QC Report**
6. **Run Settings**

The user interface supports synchronized review of corresponding input and output records and visual highlighting of allele mismatches.

## Validation Summary

| Locus or locus group | Two-field concordance |
|---|---:|
| HLA-A | 94.4% |
| HLA-B | 92.7% |
| HLA-C | 96.0% |
| HLA-DRB1 | 84.2% |
| HLA-DRB3/4/5 | 92.3% |
| HLA-DQB1 | 91.7% |
| HLA-DQA1 | 95.8% |
| Overall non-DP concordance | 92.4% |

The A/B/C/DRB1/DQB1 configuration completed 11,547 of 11,551 attempted records.

## DP Limitation

| Input condition | DPA1 | DPB1 |
|---|---:|---:|
| No DP input | 77.7% | 47.6% |
| One-field DPB1 included | 93.7% | 88.9% |

Without DP input, DPB1 concordance was substantially lower than concordance at the evaluated non-DP loci. Adding one-field DPB1 changes the task to refinement within a known DPB1 first-field family.

Do not rely on imputed DPA1/DPB1 when direct DP typing is required.

## Validation Limitations

- Internal, single-center evaluation
- Simulated one-field input generated from NGS typing
- No independent external validation
- Missing population-category information for approximately half of records
- Small population strata
- Locus-dependent performance
- No validation of clinical outcomes
- No validation of antibody classification
- No validation of downstream molecular-mismatch agreement
- No head-to-head same-cohort comparator analysis

---

# Additional Functionality Under Development

The following capabilities are implemented in the repository or actively being developed. They are **outside the manuscript-validated workflow** and must not be assigned the concordance values reported above unless separately evaluated.

## Additional Input Handling

Implemented pathways include:

- Serologic input conversion
- Raw, one-field, and two-field molecular handling
- Resolution reduction
- G-group conversion where supported
- Expanded normalization and cleaning
- Single-record and batch processing
- Configurable input-locus combinations

Serologic conversion is implemented for HLA-A, -B, -C, -DRB1, and -DQB1. Ambiguous antigens can expand into multiple molecular candidates, with an implementation limit of 50 variants per base record.

Serologic-input accuracy was not established by the manuscript.

## Additional Matching Modes

### Permissive Matching

Permissive matching can return candidates that do not reproduce every selected input locus.

Candidates are selected by:

1. Lowest mismatch count
2. Highest frequency-product score among candidates with that mismatch count

Every permissive result should be reviewed in **Mismatch Review**.

### Iterative Fallback

Optional iteration removes enabled loci from the end of the configured search order while preserving the first three search positions. The search retries until a result is found or only the protected positions remain.

Search order therefore affects iterative results. Iterative performance was not evaluated in the manuscript.

## Additional Audit Functionality

The current repository adds a seventh export sheet:

7. **Cleaning Audit**

This sheet records per-record cleaning and normalization actions. Retain it with the other output sheets when using development functionality.

## DRB3/4/5 Handling

The implementation distinguishes blank values from explicit absent-copy notation.

- Blank fields can represent unavailable or hemizygous information.
- Explicit absent-copy notation may be interpreted according to locus context.
- Internal absent-copy values can use an `DRBX*NNNN` placeholder.
- Cleaning actions are recorded for review.

Inspect transformed values and audit messages whenever DRB3, DRB4, or DRB5 is included.

## Development-Feature Cautions

- Additional modes require separate validation.
- Serologic conversion adds ambiguity before imputation.
- Permissive matching may not preserve every submitted allele.
- Iterative fallback depends on locus order.
- Conversion success does not establish biological correctness.
- Completion does not establish concordance.
- Repository functionality may change as development continues.

---

# Installation

## Executable

Download the executable and associated resources from:

https://zenodo.org/records/21414456

Keep the executable and required reference resources together.

## Build from Source

The repository contains a Windows Presentation Foundation application targeting:

```text
.NET 8.0 for Windows
```

Identified dependencies include:

```text
ClosedXML 0.105.0
Microsoft.Data.Sqlite 8.0.5
```

```bash
git clone https://github.com/mae92/HLA-Imputation-Tool.git
cd HLA-Imputation-Tool
dotnet restore
dotnet build --configuration Release
```

Reference resources must remain in the locations expected by the application.

# Quality-Control Checklist

Before interpreting results:

- Confirm input type and resolution.
- Confirm selected loci and search order.
- Confirm strict or permissive matching.
- Confirm whether iteration was enabled.
- Inspect transformed values.
- Review cleaning actions.
- Review mismatches and failures.
- Confirm the population-frequency setting.
- Retain the complete workbook.
- Report whether the analysis used validated or development functionality.

# Citation

## Software

Use the exact release citation exported from:

https://zenodo.org/records/21414456

## Manuscript

```text
Ellison MA, Xu Q, Zeevi A. HLA Imputation Tool: a locally deployable
application for batch imputation of extended HLA genotypes from one-field
molecular typing. Manuscript under review.
```

## NMDP Reference

```text
Gragert L, Madbouly A, Bashyal P, Wadsworth K, Kempenich J, Bolon Y-T,
Maiers M. Classical HLA allele and haplotype frequency estimates in US
populations. Human Immunology. 2026;87:111792.
```

# Authors

- Mitchell Ellison — co-first and corresponding author
- Qingyong Xu — co-first author
- Adriana Zeevi

University of Pittsburgh Medical Center  
Histocompatibility Laboratory  
Pittsburgh, Pennsylvania, USA

# Contributing

Contributions should preserve reproducibility, per-record auditability, strict-mode behavior, compatibility with distributed templates, and separation of parsing, normalization, searching, ranking, QC, and export.

New functionality should be clearly labeled as validated, experimental, or under development. Do not submit protected health information to public issues.

# Disclaimer

HLA Imputation Tool is research software. Users are responsible for validating performance for their intended workflow, reviewing all QC information, and distinguishing validated settings from additional development functionality. Imputed assignments must not be represented as directly observed typing or used as the sole basis for clinical decisions.
