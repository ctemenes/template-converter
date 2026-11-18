"""Builds the sample legacy Word templates in samples/legacy.

Every file is written as raw WordprocessingML so the field codes are exactly
what Word's mail merge produces: complex fields (begin / instrText / separate /
end), nested IF fields, TableStart/TableEnd repeat markers, and also the
hand-typed placeholder conventions people use when they do not use fields.
All company names and details are made up.
"""
import os, zipfile
from xml.sax.saxutils import escape

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'samples', 'legacy')
W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"'

def rpr(b=False, i=False):
    if not (b or i): return ''
    return '<w:rPr>%s%s</w:rPr>' % ('<w:b/>' if b else '', '<w:i/>' if i else '')

def r(text, b=False, i=False):
    return '<w:r>%s<w:t xml:space="preserve">%s</w:t></w:r>' % (rpr(b, i), escape(text))

def _fc(kind): return '<w:r><w:fldChar w:fldCharType="%s"/></w:r>' % kind
def _instr(text): return '<w:r><w:instrText xml:space="preserve">%s</w:instrText></w:r>' % escape(text)

def mf(name, switch=''):
    """A real MERGEFIELD complex field with a cached result."""
    return (_fc('begin') + _instr(' MERGEFIELD %s %s' % (name, switch)) + _fc('separate')
            + r('«%s»' % name) + _fc('end'))

def sysf(name):
    return _fc('begin') + _instr(' %s ' % name) + _fc('separate') + r('1') + _fc('end')

def iff(left, op, right, when_true, when_false):
    """IF field. when_true / when_false are lists of str or ('mf', name)."""
    def parts(items):
        out = ''
        for it in items:
            out += _instr(it) if isinstance(it, str) else mf(it[1])
        return out
    return (_fc('begin') + _instr(' IF ') + mf(left) + _instr(' %s "%s" "' % (op, right))
            + parts(when_true) + _instr('" "') + parts(when_false) + _instr('" ')
            + _fc('separate') + r('...') + _fc('end'))

def p(*runs, style=None, bullet=False, page_break_before=False):
    ppr = ''
    if style: ppr += '<w:pStyle w:val="%s"/>' % style
    if bullet: ppr += '<w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr>'
    if page_break_before: ppr += '<w:pageBreakBefore/>'
    body = ''.join(x if x.startswith('<w:') else r(x) for x in runs)
    return '<w:p>%s%s</w:p>' % ('<w:pPr>%s</w:pPr>' % ppr if ppr else '', body)

def tbl(rows):
    out = '<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/></w:tblPr>'
    for row in rows:
        out += '<w:tr>' + ''.join('<w:tc>%s</w:tc>' % c for c in row) + '</w:tr>'
    return out + '</w:tbl>'

STYLES = '''<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles %s>
<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:rPr><w:sz w:val="22"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:rPr><w:b/><w:sz w:val="26"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Address"><w:name w:val="Address"/><w:basedOn w:val="Normal"/></w:style>
<w:style w:type="paragraph" w:styleId="SmallPrint"><w:name w:val="Small Print"/><w:basedOn w:val="Normal"/><w:rPr><w:i/><w:sz w:val="16"/></w:rPr></w:style>
</w:styles>''' % W

def write(name, body, footer=None, landscape=False):
    sect = '<w:sectPr>%s<w:pgSz %s/><w:pgMar w:top="1440" w:right="1134" w:bottom="1440" w:left="1134" w:header="708" w:footer="708"/></w:sectPr>' % (
        '<w:footerReference w:type="default" r:id="rId2"/>' if footer else '',
        'w:w="16838" w:h="11906" w:orient="landscape"' if landscape else 'w:w="11906" w:h="16838"')
    doc = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document %s><w:body>%s%s</w:body></w:document>' % (W, body, sect)
    types = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
             '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>'
             '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
             '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>'
             + ('<Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>' if footer else '')
             + '</Types>')
    rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
    drels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
             '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>'
             + ('<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>' if footer else '')
             + '</Relationships>')
    os.makedirs(OUT, exist_ok=True)
    with zipfile.ZipFile(os.path.join(OUT, name), 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml', types); z.writestr('_rels/.rels', rels)
        z.writestr('word/document.xml', doc); z.writestr('word/styles.xml', STYLES)
        z.writestr('word/_rels/document.xml.rels', drels)
        if footer:
            z.writestr('word/footer1.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:ftr %s>%s</w:ftr>' % (W, footer))

# 1. Real mail-merge fields and IF fields.
write('01-policy-renewal-notice.docx', ''.join([
    p('Your policy is due for renewal', style='Heading1'),
    p(mf('Letter_Date', '\\@ "d MMMM yyyy"')),
    p(mf('Title'), ' ', mf('First_Name'), ' ', mf('Last_Name'), style='Address'),
    p(mf('Address_Line_1'), style='Address'),
    p(mf('Suburb'), ' ', mf('State'), ' ', mf('Postcode'), style='Address'),
    p('Dear ', mf('First_Name'), ','),
    p('Your ', mf('Product_Name'), ' policy ', mf('Policy_Number'), ' expires on ', mf('Expiry_Date', '\\@ "d MMMM yyyy"'),
      '. To keep your cover in place, your renewal premium of ', mf('Renewal_Premium', '\\# "$#,##0.00"'), ' is due by ', mf('Due_Date', '\\@ "d MMMM yyyy"'), '.'),
    p(iff('State', '=', 'QLD', ['Because your property is in Queensland, your premium includes the state emergency levy of ', ('mf', 'Levy_Amount'), '.'], [])),
    p(iff('Auto_Renew', '=', 'Y', ['We will debit your nominated account on the due date. You do not need to do anything.'],
          ['Please pay by the due date using one of the options on the back of this notice.'])),
    p('If your details have changed, call us on 1300 000 000.'),
    p('Yours sincerely,'), p(r('Customer Care Team', b=True)),
]), footer=p('Harbourline Mutual Ltd ABN 00 000 000 000. Page ', sysf('PAGE'), ' of ', sysf('NUMPAGES'), style='SmallPrint'))

# 2. Hand-typed << >> placeholders and plain-English instructions to the letter writer.
write('02-overdue-account-reminder.docx', ''.join([
    p('Overdue account reminder', style='Heading1'),
    p('<<Date>>'), p('<<Customer Name>>', style='Address'), p('<<Address>>', style='Address'),
    p(r('Account number: ', b=True), '<<Account No>>'),
    p('Dear <<Customer Name>>,'),
    p('Our records show that $<<Amount Overdue>> on your account was due on <<Due Date>> and has not been paid.'),
    p('[Include the next paragraph only if the account is more than 60 days overdue]'),
    p('As your account is now seriously overdue, we may refer it to our collections team if payment is not received within 7 days.'),
    p('[If the customer is on a hardship arrangement, delete the paragraph above and insert: We know you have a payment arrangement with us. This letter is a reminder only.]'),
    p('You can pay online at northgate.example/pay or call us on 13 00 00.'),
    p('Regards,'), p(r('Accounts Team', b=True)),
]))

# 3. A repeating table row marked with TableStart / TableEnd, and a numeric IF.
write('03-account-statement.docx', ''.join([
    p('Account statement', style='Heading1'),
    p('Statement period: ', mf('Period_Start', '\\@ "d MMM yyyy"'), ' to ', mf('Period_End', '\\@ "d MMM yyyy"')),
    p(r('Account: ', b=True), mf('Account_Number')), p(r('Name: ', b=True), mf('Account_Name')),
    tbl([[p(r('Date', b=True)), p(r('Description', b=True)), p(r('Amount', b=True))],
         [p(mf('TableStart:Transactions'), mf('Txn_Date', '\\@ "dd/MM/yyyy"')), p(mf('Txn_Description')), p(mf('Txn_Amount', '\\# "$#,##0.00"'), mf('TableEnd:Transactions'))]]),
    p('Opening balance: ', mf('Opening_Balance', '\\# "$#,##0.00"')),
    p('Closing balance: ', mf('Closing_Balance', '\\# "$#,##0.00"')),
    p(iff('Closing_Balance', '>', '0', ['Please pay ', ('mf', 'Closing_Balance'), ' by ', ('mf', 'Due_Date'), '.'],
          ['Your account is in credit. No payment is needed.'])),
]), footer=p('Northgate Energy. Page ', sysf('PAGE'), style='SmallPrint'), landscape=True)

# 4. Mixed conventions in one file, and an either/or instruction.
write('04-claim-outcome-letter.docx', ''.join([
    p('Outcome of your claim {{claim_number}}', style='Heading1'),
    p('[DATE]'), p('Dear [Customer First Name],'),
    p('We have finished assessing your claim for {{loss_type}} on {{loss_date}}.'),
    p('[Use paragraph A if the claim was approved, or paragraph B if it was declined]'),
    p('A. We are pleased to confirm your claim has been approved. We will pay ${{settlement_amount}} to your nominated account within 5 business days, less your excess of ${{excess}}.'),
    p('B. Unfortunately we are unable to accept your claim. The reason is: {{decline_reason}}. You can ask for a review of this decision within 30 days.'),
    p('Your claims consultant is [Consultant Name], who you can reach on [Consultant Phone].'),
    p('Yours sincerely,'), p(r('Claims Team', b=True)),
]))

# 5. Typed chevrons, headings, a bullet list, an optional section and a page break.
write('05-welcome-letter.docx', ''.join([
    p('Welcome to Harbourline', style='Heading1'),
    p('Dear «First Name»,'),
    p('Thank you for choosing us for your «Product» cover. Your policy number is «Policy No» and your cover starts on «Start Date».'),
    p('What happens next', style='Heading2'),
    p('Read your policy schedule and check your details.', bullet=True),
    p('Keep this letter with your policy documents.', bullet=True),
    p('Set up your online account at harbourline.example/register.', bullet=True),
    p('[Delete this section if the customer does not pay by direct debit]'),
    p('Your direct debit', style='Heading2'),
    p('We will debit «Instalment Amount» from your account ending in «Account Last 4» on the «Debit Day» of each month.'),
    p('How to contact us', style='Heading2', page_break_before=True),
    p('Phone 1300 000 000, Monday to Friday, 8am to 6pm.'),
    p('Email care@harbourline.example.'),
]))

# 6. An instruction no tool should decide on its own. The right outcome is "needs review".
write('06-fee-change-notice.docx', ''.join([
    p('Changes to your account fees', style='Heading1'),
    p(mf('Letter_Date', '\\@ "d MMMM yyyy"')),
    p('Dear ', mf('First_Name'), ','),
    p('From ', mf('Effective_Date', '\\@ "d MMMM yyyy"'), ' the monthly fee on your ', mf('Plan_Name'), ' plan will change from ', mf('Old_Fee', '\\# "$#,##0.00"'), ' to ', mf('New_Fee', '\\# "$#,##0.00"'), '.'),
    p('[Check with Legal which wording applies to grandfathered plans before sending]'),
    p('If you would like to talk about other plans, call us on 13 00 00.'),
    p('Regards,'), p(r('Customer Care Team', b=True)),
]))
print('wrote', sorted(os.listdir(OUT)))
