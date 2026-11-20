#!/usr/bin/env python3
"""Create an Excel-safe two-field HLA allele-to-serology reference from WMDA rel_dna_ser.txt."""
import argparse, csv
from collections import defaultdict
from pathlib import Path

TYPE_ORDER={'Unambiguous':0,'Possible':1,'Assumed':2}

def two_field(a):
    p=a.strip().split(':')
    return ':'.join(p[:2])

def vals(x):
    return [v.strip() for v in x.split('/') if v.strip() and v.strip()!='?'] if x else []

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('input',type=Path)
    ap.add_argument('-o','--output',type=Path,default=Path('Allele_to_Serology_TwoField_IMGTHLA.csv'))
    args=ap.parse_args()

    evidence=defaultdict(set)  # (full two-field allele, antigen) -> evidence types
    observed=defaultdict(set)  # full two-field allele -> antigens across source alleles
    full_by_two=defaultdict(lambda: defaultdict(set))

    with args.input.open(encoding='utf-8-sig') as f:
        for n,line in enumerate(f,1):
            if not line.strip() or line.startswith('#'): continue
            p=line.rstrip('\r\n').split(';')
            if len(p)!=7: raise ValueError(f'Line {n}: expected 7 fields, got {len(p)}')
            gene,a,u,poss,assumed,_exceptions,_hats=p
            a2=two_field(a)
            allele=f'{gene}{a2}'  # e.g. A*01:01; safely remains text in Excel
            assignment=('Unambiguous',u) if u else ('Possible',poss) if poss else ('Assumed',assumed)
            typ,raw=assignment
            for ag in vals(raw):
                evidence[(allele,ag)].add(typ)
                observed[allele].add(ag)
                full_by_two[allele][a].add(ag)

    rows=[]
    for (allele,ag),types in evidence.items():
        # A conflict means full-resolution descendants of the same two-field allele
        # do not all share the same serologic assignment. This is especially important
        # when expressed and N/L/Q descendants collapse to the same two-field name.
        desc_sets={tuple(sorted(x)) for x in full_by_two[allele].values()}
        conflict='Yes' if len(desc_sets)>1 else 'No'
        t='|'.join(sorted(types,key=TYPE_ORDER.get))
        rows.append((allele,ag,t,conflict))

    rows.sort(key=lambda r:(r[0].split('*',1)[0], r[0], r[1]))
    with args.output.open('w',encoding='utf-8',newline='') as f:
        w=csv.writer(f,lineterminator='\n')
        w.writerow(['Allele','Antigen','AssignmentType','TwoFieldConflict'])
        w.writerows(rows)
    print(f'{len(rows):,} rows -> {args.output}')
    print(f'{sum(1 for r in rows if r[3]=="Yes"):,} mapping rows are in two-field conflict groups')

if __name__=='__main__': main()
