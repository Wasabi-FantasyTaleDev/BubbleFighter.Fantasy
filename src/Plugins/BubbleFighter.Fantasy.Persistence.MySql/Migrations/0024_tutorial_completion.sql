-- Offering a tutorial does not prove that the client finished it.
ALTER TABLE player_onboarding ADD COLUMN tutorial_completed TINYINT NOT NULL DEFAULT 0;
