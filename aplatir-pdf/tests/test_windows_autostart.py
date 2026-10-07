"""Démarrage automatique avec Windows (clé HKCU\\...\\Run), testé sans Windows grâce à un faux ``winreg``.

Le faux registre est injecté dans ``sys.modules`` et ``sys.platform`` vaut « win32 » le temps du test : on ne
touche donc jamais au vrai registre (y compris sur le runner Windows de l'intégration continue).
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant WIN-n du rapport).
"""
import sys
import types

import pytest

tray = pytest.importorskip("aplatir_tray")

RUN = tray.CLE_RUN
APPROUVE = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
ACTIVE = bytes([0x02]) + bytes(11)          # valeur écrite par Windows quand l'entrée est activée
DESACTIVE = bytes([0x03]) + bytes(11)       # idem quand l'utilisateur la désactive (Gestionnaire des tâches...)


class _Cle:
    def __init__(self, chemin):
        self.chemin = chemin

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class FauxWinreg(types.ModuleType):
    HKEY_CURRENT_USER = "HKCU"
    KEY_SET_VALUE = 0x2
    KEY_READ = 0x20019
    REG_SZ = 1
    REG_BINARY = 3

    def __init__(self):
        super().__init__("winreg")
        self.cles = {}      # (ruche, chemin en minuscules) -> {nom: (valeur, type)}

    def _id(self, ruche, chemin):
        return (ruche, chemin.lower())

    def OpenKey(self, ruche, chemin, reserve=0, acces=0):
        k = self._id(ruche, chemin)
        if k not in self.cles:
            raise FileNotFoundError(2, "clé absente", chemin)
        return _Cle(k)

    def CreateKeyEx(self, ruche, chemin, reserve=0, acces=0):
        k = self._id(ruche, chemin)
        self.cles.setdefault(k, {})
        return _Cle(k)

    def QueryValueEx(self, cle, nom):
        valeurs = self.cles[cle.chemin]
        if nom not in valeurs:
            raise FileNotFoundError(2, "valeur absente", nom)
        return valeurs[nom]

    def SetValueEx(self, cle, nom, reserve, type_, valeur):
        self.cles[cle.chemin][nom] = (valeur, type_)

    def DeleteValue(self, cle, nom):
        if nom not in self.cles[cle.chemin]:
            raise FileNotFoundError(2, "valeur absente", nom)
        del self.cles[cle.chemin][nom]

    # aides de test
    def valeur(self, chemin, nom):
        return self.cles.get(self._id(self.HKEY_CURRENT_USER, chemin), {}).get(nom, (None, None))[0]

    def poser(self, chemin, nom, valeur, type_):
        self.cles.setdefault(self._id(self.HKEY_CURRENT_USER, chemin), {})[nom] = (valeur, type_)


@pytest.fixture
def reg(monkeypatch):
    faux = FauxWinreg()
    monkeypatch.setitem(sys.modules, "winreg", faux)
    monkeypatch.setattr(sys, "platform", "win32")
    return faux


# --------------------------------------------------------------------------- #
# Régression : ce qui marche
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("exe", [
    r"C:\Outils\AplatirPDF\AplatirPDF.exe",
    r"C:\Users\Zoé Dupont\OneDrive - Société Générale\Outils PDF\AplatirPDF.exe",
    r"\\serveur\partage\Qualité\AplatirPDF.exe",
])
def test_commande_de_demarrage_entre_guillemets_avec_espaces_et_accents(monkeypatch, exe):
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", exe)
    assert tray.commande_demarrage() == f'"{exe}" --tray'


def test_aller_retour_du_registre(reg, monkeypatch):
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", r"C:\Outils é\AplatirPDF.exe")
    assert not tray.demarrage_actif()
    assert tray.regler_demarrage(True)
    assert reg.valeur(RUN, tray.ID_APP) == '"C:\\Outils é\\AplatirPDF.exe" --tray'
    assert reg.cles[("HKCU", RUN.lower())][tray.ID_APP][1] == reg.REG_SZ
    assert tray.demarrage_actif()
    assert tray.regler_demarrage(False)
    assert not tray.demarrage_actif()
    assert tray.regler_demarrage(False), "retirer une entrée absente ne doit pas échouer"


def test_acces_registre_refuse_rend_false(reg, monkeypatch):
    def refuse(*a, **k):
        raise PermissionError(5, "Accès refusé")
    monkeypatch.setattr(reg, "CreateKeyEx", refuse)
    assert tray.regler_demarrage(True) is False


# --------------------------------------------------------------------------- #
# WIN-1 : « désactivé » par Windows (StartupApproved) n'est ni vu ni levé
# --------------------------------------------------------------------------- #
def test_demarrage_actif_tient_compte_du_veto_de_windows(reg):
    assert tray.regler_demarrage(True)
    reg.poser(APPROUVE, tray.ID_APP, DESACTIVE, reg.REG_BINARY)
    assert tray.demarrage_actif() is False, "l'outil affiche « lancé au démarrage » alors que Windows ne le lance plus"


def test_reactiver_leve_le_veto_de_windows(reg):
    assert tray.regler_demarrage(True)
    reg.poser(APPROUVE, tray.ID_APP, DESACTIVE, reg.REG_BINARY)
    assert tray.regler_demarrage(True)
    veto = reg.valeur(APPROUVE, tray.ID_APP)
    assert veto is None or veto[0] % 2 == 0, f"le veto de Windows est resté en place : {veto!r}"


def test_reactiver_n_ecrase_pas_une_entree_deja_approuvee(reg):
    """Garde-fou du correctif de WIN-1 : une entrée approuvée (02...) doit le rester."""
    assert tray.regler_demarrage(True)
    reg.poser(APPROUVE, tray.ID_APP, ACTIVE, reg.REG_BINARY)
    assert tray.regler_demarrage(True)
    veto = reg.valeur(APPROUVE, tray.ID_APP)
    assert veto is None or veto[0] % 2 == 0
    assert tray.demarrage_actif()
